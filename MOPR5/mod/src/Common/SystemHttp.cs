// MOP - REVIVAL
// Copyright(C) 2026 ANICKON
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// HTTPS GET through Windows' WinHTTP, on a background thread (5.1.1, for the update check).
//
// The game can't reach today's HTTPS servers by itself: Unity 5.0's WWW answers GitHub with "SSL23_GET_SERVER_HELLO:
// tlsv1 alert protocol version" (TestBot update-check), and Mono 2.0's WebClient has no TLS 1.2 either.
// WinINet doesn't work in the game either (error 127, ERROR_PROC_NOT_FOUND): the game folder has MSCLoader's
// winhttp.dll (the Doorstop loader), which forwards 82 of the 92 WinHTTP functions, and WinINet needs some of the
// missing ones. The WinHTTP functions used here are all forwarded to the system's winhttp.dll, so this works
// with or without that loader. TLS 1.2 and 1.3 are asked for (Windows 8.1 and newer have them by default;
// Windows 7 needs update KB3140245). Nothing is cached or stored, no cookies.

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace MOP.Common
{
    static class SystemHttp
    {
        /// <summary>One request; Done turns true on the worker thread when Text or Error is set.</summary>
        internal sealed class Request
        {
            volatile bool done;
            public bool Done => done;
            public string Text { get; private set; }
            public string Error { get; private set; }
            public int Status { get; private set; }

            internal void Finish(string text, string error, int status)
            {
                Text = text;
                Error = error;
                Status = status;
                done = true;
            }
        }

        const int AccessDefaultProxy = 0, AccessAutomaticProxy = 4;     // AUTOMATIC: Windows 8.1 and newer
        const int FlagSecure = 0x00800000;
        const int OptionSecureProtocols = 84, OptionDisableFeature = 63;
        const int Tls12 = 0x00000800, Tls13 = 0x00002000;
        const int DisableCookies = 0x00000001;
        const int QueryStatusCode = 19, QueryFlagNumber = 0x20000000;
        const int MaxBytes = 1 << 20;

        [DllImport("winhttp.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        static extern IntPtr WinHttpOpen(string agent, int accessType, string proxyName, string proxyBypass, int flags);

        [DllImport("winhttp.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        static extern IntPtr WinHttpConnect(IntPtr session, string serverName, ushort port, int reserved);

        [DllImport("winhttp.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        static extern IntPtr WinHttpOpenRequest(IntPtr connect, string verb, string objectName, string version, string referrer,
                                                IntPtr acceptTypes, int flags);

        [DllImport("winhttp.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        static extern bool WinHttpSendRequest(IntPtr request, string headers, uint headersLength, IntPtr optional,
                                              uint optionalLength, uint totalLength, IntPtr context);

        [DllImport("winhttp.dll", ExactSpelling = true, SetLastError = true)]
        static extern bool WinHttpReceiveResponse(IntPtr request, IntPtr reserved);

        [DllImport("winhttp.dll", ExactSpelling = true, SetLastError = true)]
        static extern bool WinHttpQueryHeaders(IntPtr request, int infoLevel, IntPtr name, ref int buffer, ref int bufferLength, IntPtr index);

        [DllImport("winhttp.dll", ExactSpelling = true, SetLastError = true)]
        static extern bool WinHttpReadData(IntPtr request, byte[] buffer, int toRead, out int read);

        [DllImport("winhttp.dll", ExactSpelling = true, SetLastError = true)]
        static extern bool WinHttpSetTimeouts(IntPtr handle, int resolve, int connect, int send, int receive);

        [DllImport("winhttp.dll", ExactSpelling = true, SetLastError = true)]
        static extern bool WinHttpSetOption(IntPtr handle, int option, ref int buffer, int length);

        [DllImport("winhttp.dll", ExactSpelling = true, SetLastError = true)]
        static extern bool WinHttpCloseHandle(IntPtr handle);

        /// <summary>Starts an HTTPS GET on a background thread. headers: "Name: value\r\n" lines or null.</summary>
        internal static Request Get(string url, string userAgent, string headers, int timeoutMs)
        {
            Request request = new Request();
            Thread thread = new Thread(() => Run(request, url, userAgent, headers, timeoutMs))
            {
                IsBackground = true,   // never keeps the game from quitting
                Name = "MOPR HTTP"
            };
            thread.Start();
            return request;
        }

        static void Run(Request result, string url, string userAgent, string headers, int timeoutMs)
        {
            IntPtr session = IntPtr.Zero, connect = IntPtr.Zero, request = IntPtr.Zero;
            try
            {
                Uri uri = new Uri(url);
                if (uri.Scheme != "https") { result.Finish(null, "only https is supported", 0); return; }

                session = WinHttpOpen(userAgent, AccessAutomaticProxy, null, null, 0);
                if (session == IntPtr.Zero) session = WinHttpOpen(userAgent, AccessDefaultProxy, null, null, 0);
                if (session == IntPtr.Zero) { Fail(result, "WinHttpOpen"); return; }

                WinHttpSetTimeouts(session, timeoutMs, timeoutMs, timeoutMs, timeoutMs);
                int protocols = Tls12 | Tls13;
                if (!WinHttpSetOption(session, OptionSecureProtocols, ref protocols, 4))
                {
                    protocols = Tls12;   // systems that don't know TLS 1.3
                    WinHttpSetOption(session, OptionSecureProtocols, ref protocols, 4);
                }

                connect = WinHttpConnect(session, uri.Host, (ushort)uri.Port, 0);
                if (connect == IntPtr.Zero) { Fail(result, "WinHttpConnect"); return; }

                request = WinHttpOpenRequest(connect, "GET", uri.PathAndQuery, null, null, IntPtr.Zero, FlagSecure);
                if (request == IntPtr.Zero) { Fail(result, "WinHttpOpenRequest"); return; }
                int noCookies = DisableCookies;
                WinHttpSetOption(request, OptionDisableFeature, ref noCookies, 4);

                if (!WinHttpSendRequest(request, headers, headers != null ? 0xFFFFFFFF : 0, IntPtr.Zero, 0, 0, IntPtr.Zero))
                {
                    Fail(result, "WinHttpSendRequest");
                    return;
                }
                if (!WinHttpReceiveResponse(request, IntPtr.Zero)) { Fail(result, "WinHttpReceiveResponse"); return; }

                int status = 0, size = 4;
                WinHttpQueryHeaders(request, QueryStatusCode | QueryFlagNumber, IntPtr.Zero, ref status, ref size, IntPtr.Zero);

                using (MemoryStream body = new MemoryStream())
                {
                    byte[] buffer = new byte[8192];
                    int read;
                    while (WinHttpReadData(request, buffer, buffer.Length, out read) && read > 0)
                    {
                        body.Write(buffer, 0, read);
                        if (body.Length > MaxBytes) { result.Finish(null, "answer too large", status); return; }
                    }
                    string text = Encoding.UTF8.GetString(body.ToArray());
                    result.Finish(text, status == 200 ? null : "HTTP " + status, status);
                }
            }
            catch (Exception ex)
            {
                result.Finish(null, ex.GetType().Name + ": " + ex.Message, 0);
            }
            finally
            {
                if (request != IntPtr.Zero) WinHttpCloseHandle(request);
                if (connect != IntPtr.Zero) WinHttpCloseHandle(connect);
                if (session != IntPtr.Zero) WinHttpCloseHandle(session);
            }
        }

        static void Fail(Request result, string step) => result.Finish(null, step + ": " + Describe(Marshal.GetLastWin32Error()), 0);

        static string Describe(int code)
        {
            switch (code)
            {
                case 12002: return "timeout (12002)";
                case 12007: return "server name not resolved, offline? (12007)";
                case 12029: return "cannot connect (12029)";
                case 12157: return "secure channel error (12157)";
                case 12175: return "secure connection failed, TLS 1.2 missing? (12175)";
                default: return "error " + code;
            }
        }
    }
}
