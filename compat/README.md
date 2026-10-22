# Compatibility list

`mods.json` is the only place to edit: the website (`site/`, published by `.github/workflows/site.yml`) is built
from it and from the built-in rule files (`src/MOPR/Rules/<ModID>.json`) by `scripts/build-site.ps1`.

## Entry

```json
{ "id": "YAIM", "name": "Yet Another Inventory Mod", "author": "Ceres et al.",
  "url": "https://www.nexusmods.com/mysummercar/mods/7483",
  "status": "fix", "code": true, "since": "5.1.1", "tested": "2026-10-22",
  "notes": { "en": "...", "ru": "...", "pl": "..." } }
```

| Field | |
|---|---|
| `id` | MSCLoader mod ID. Same as the rule file name, if there is one. Required. |
| `status` | `works` (tested, nothing needed), `fix` (MOPR adjusts itself: a rule file or code), `partial` (works with a limitation, say which in `notes`), `incompatible`, `untested`. Required. |
| `name`, `author`, `url` | Optional. Without `url` the site links to a Nexus search. |
| `code` | `true` when MOPR has code for the mod (not only rules). |
| `origin` | `"mop"`: inherited from the original MOP (its rule database or its code). |
| `since` | First MOPR version with the fix. Default `5.0.0` for `origin: "mop"`. |
| `tested` | `yyyy-MM-dd` of the last in-game check with MOPR 5.x. Empty = not re-tested. |
| `notes` | Short text per language; missing languages fall back to English. |
| `aliases` | Other IDs to find the mod by. |

The build fails when a built-in rule file has no entry, an ID is listed twice, a status is unknown, or a `fix` has
neither rules that do something nor `"code": true`.

## Preview

```powershell
.\scripts\build-site.ps1
python -m http.server 8765 --directory site     # then open http://127.0.0.1:8765
```
