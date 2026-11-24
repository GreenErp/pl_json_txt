# How to Run the M3U Link Checker

This guide explains how to run the concurrent M3U link validator on your playlist files.

---

## 1. Quick Start (Default)

By default, the script reads `index.m3u` and writes verified, playable streams to `index1.m3u`.

### Option A: Standard PowerShell
Open PowerShell in this directory (`c:\Users\badar\Work\Green\pl_json_txt`) and run:

```powershell
.\run_checker.ps1
```

### Option B: If PowerShell Blocks Script Execution
If Windows prevents running scripts (`ExecutionPolicy` restriction), run with `-ExecutionPolicy Bypass`:

```powershell
powershell -ExecutionPolicy Bypass -File .\run_checker.ps1
```

---

## 2. Custom Parameters & Options

The script supports customizable parameters:

| Parameter | Type | Default | Description |
| :--- | :--- | :--- | :--- |
| `-InputFile` | string | `"index.m3u"` | Path to the source M3U file to test |
| `-OutputFile` | string | `"index1.m3u"` | Path where verified working channels will be written |
| `-Concurrency` | int | `150` | Number of simultaneous connections |
| `-TimeoutSeconds` | int | `5` | Maximum seconds to wait before marking a link dead |

---

## 3. Examples

### Check a different file and save to a new name
```powershell
.\run_checker.ps1 -InputFile "pl_nl.m3u" -OutputFile "pl_nl_working.m3u"
```

### Run with higher speed (200 workers, 4-second timeout)
```powershell
.\run_checker.ps1 -Concurrency 200 -TimeoutSeconds 4
```

### Re-check the generated file
To re-test `index1.m3u` and output to `index2.m3u`:
```powershell
.\run_checker.ps1 -InputFile "index1.m3u" -OutputFile "index2.m3u"
```

---

## 4. How It Works & What It Filters

1. **Payload & HTML Error Filtering:**
   - Detects and rejects fake 200 OK responses that return HTML error pages, Cloudflare challenge pages, iframe wrappers, or landing pages instead of real video streams.
   - For `.m3u8` playlists, verifies the payload actually starts with `#EXTM3U` or contains `#EXT-X-` tags.
   - For `.mpd` (DASH) manifests, verifies XML `<MPD>` structure.
   - For binary media (TS, MP4, MKV, FLV), validates media sync bytes (e.g. `0x47` TS sync byte, `ftyp` MP4 header, Matroska header).
2. **Full Metadata Preservation:**
   - Preserves channel logos, group titles, TVG IDs, names, and custom headers (`#EXTVLCOPT` user-agents and referrers).
3. **High-Speed Concurrency:**
   - Uses 150 parallel workers with lightweight header/snippet reads (no full stream download required).
4. **Live Progress:**
   - Displays real-time progress, completion percentage, count of verified streams, and elapsed time every 500 channels.
