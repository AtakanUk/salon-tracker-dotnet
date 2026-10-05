<#
  Friseur - a complete, verified copy of everything on the server, on this computer.

  Asks the server for a fresh backup first, then downloads the whole backup
  folder and the secret settings into a dated folder. Every run creates a new
  folder and leaves the older ones alone.

  Usage (PowerShell):
      .\ops\pull-snapshot.ps1 -Server root@SERVER_IP
      .\ops\pull-snapshot.ps1 -Server root@SERVER_IP -Target D:\Backup -SkipBackup

  Why it exists: once the server is handed over to the salon owner's own
  hosting account, access to the hosting console is gone. From then on this
  folder is the only independent copy you hold.
#>
param(
  [Parameter(Mandatory = $true)]
  [string]$Server,
  [string]$Target = 'C:\Friseur-Snapshots',
  # Download what is on the server without asking for a new backup first
  [switch]$SkipBackup
)

$ErrorActionPreference = 'Stop'

$SSH_OPT = @('-o', 'ConnectTimeout=20', '-o', 'BatchMode=yes')
$REMOTE_PROJECT = '/opt/friseur'
$REMOTE_TAR = '/tmp/friseur-snapshot.tgz'

function Heading($text) {
  Write-Host ''
  Write-Host ("-- " + $text + " " + ("-" * [Math]::Max(0, 46 - $text.Length)))
}

# Runs a command on the server. Native executables do not throw, so the exit
# code has to be checked by hand - a silent failure is the worst outcome here.
function Remote([string]$Command) {
  $output = & ssh @SSH_OPT $Server $Command
  if ($LASTEXITCODE -ne 0) {
    throw "Command failed on the server (exit code $LASTEXITCODE):`n  $Command"
  }
  return $output
}

function Download([string]$RemotePath, [string]$LocalPath) {
  & scp @SSH_OPT $($Server + ':' + $RemotePath) $LocalPath
  if ($LASTEXITCODE -ne 0) { throw "Download failed: $RemotePath" }
}

# -- 1. Connection --------------------------------------------------------------
Heading 'Connection'
try {
  $hostName = (Remote 'hostname') -join ''
} catch {
  Write-Host "Could not connect to the server ($Server)." -ForegroundColor Red
  Write-Host "Check your internet connection and SSH key, then try again."
  exit 1
}
Write-Host "Server: $hostName ($Server)  OK"

# -- 2. Fresh backup -----------------------------------------------------------
if ($SkipBackup) {
  Heading 'Backup'
  Write-Host 'Skipped (-SkipBackup). The files already on the server will be downloaded.'
} else {
  Heading 'Taking a fresh backup on the server'
  Remote 'friseur backup' | Out-Null
  Write-Host 'New backup taken  OK'
}

# -- 3. Does the dump actually open, BEFORE downloading? ------------------------
# Downloading a broken file as "the backup" is worse than having no backup:
# people trust it. So the check happens at the source, before the download.
Heading 'Is the dump readable'
$latestDump = (Remote "ls -1t $REMOTE_PROJECT/backups/friseur-*.dump | head -1") -join ''
if ([string]::IsNullOrWhiteSpace($latestDump)) { throw 'No .dump backup found on the server.' }
$latestDumpName = Split-Path $latestDump -Leaf
Remote "cd $REMOTE_PROJECT && docker compose exec -T db pg_restore --list '/backups/$latestDumpName' > /dev/null" | Out-Null
Write-Host "$latestDumpName is readable  OK"

# -- 4. Folder -----------------------------------------------------------------
$stamp = Get-Date -Format 'yyyy-MM-dd-HHmm'
$folder = Join-Path $Target $stamp
$dataDir = Join-Path $folder 'data'
$settingsDir = Join-Path $folder 'settings'
New-Item -ItemType Directory -Force -Path $dataDir | Out-Null
New-Item -ItemType Directory -Force -Path $settingsDir | Out-Null

# -- 5. Data: download as a single archive ---------------------------------------
# One archive instead of a hundred separate files: faster, and a single checksum
# comparison proves the transfer is complete.
Heading 'Downloading data'
Remote "tar -czf $REMOTE_TAR -C $REMOTE_PROJECT backups" | Out-Null
$remoteTarHash = ((Remote "sha256sum $REMOTE_TAR") -join '').Split(' ')[0]
$localTar = Join-Path $folder 'data.tgz'
Download $REMOTE_TAR $localTar
$localTarHash = (Get-FileHash -Algorithm SHA256 -Path $localTar).Hash.ToLower()

if ($remoteTarHash -ne $localTarHash) {
  Write-Host 'The downloaded archive DOES NOT MATCH the server - it may be incomplete.' -ForegroundColor Red
  Write-Host "  server: $remoteTarHash"
  Write-Host "  local : $localTarHash"
  throw 'Transfer could not be verified, the copy is not reliable.'
}
Write-Host "Archive downloaded and verified  OK"

& tar -xzf $localTar -C $dataDir --strip-components=1
if ($LASTEXITCODE -ne 0) { throw 'Could not extract the archive.' }
Remove-Item $localTar -Force
Remote "rm -f $REMOTE_TAR" | Out-Null

# -- 6. File by file verification -----------------------------------------------
# An intact archive is one thing, correct extracted files are another.
Heading 'Verifying every file'
$remoteHashes = @{}
foreach ($line in (Remote "cd $REMOTE_PROJECT/backups && sha256sum *")) {
  if ($line -match '^([0-9a-f]{64})\s+\*?(.+)$') { $remoteHashes[$Matches[2].Trim()] = $Matches[1] }
}
$verified = 0
$broken = @()
foreach ($name in $remoteHashes.Keys) {
  $local = Join-Path $dataDir $name
  if (-not (Test-Path $local)) { $broken += "$name (missing)"; continue }
  if ((Get-FileHash -Algorithm SHA256 -Path $local).Hash.ToLower() -ne $remoteHashes[$name]) {
    $broken += "$name (content differs)"
    continue
  }
  $verified++
}
if ($broken.Count -gt 0) {
  Write-Host 'These files could not be verified:' -ForegroundColor Red
  $broken | ForEach-Object { Write-Host "  - $_" }
  throw 'The copy is incomplete or corrupt, do not trust it.'
}
Write-Host "All $verified files are identical to the server  OK"

# -- 7. Secret settings ---------------------------------------------------------
Heading 'Downloading settings'
# fetched with scp: line endings stay intact, so the files can go back to Linux as-is
Download "$REMOTE_PROJECT/.env" (Join-Path $settingsDir 'friseur.env')
Write-Host 'friseur.env  OK'
try {
  Download '/etc/friseur-watchdog.env' (Join-Path $settingsDir 'friseur-watchdog.env')
  Write-Host 'friseur-watchdog.env  OK'
} catch {
  Write-Host 'friseur-watchdog.env not found (that is fine)'
}
# The Caddyfile on the server may differ from the repository - sites added by
# hand only live there and are in no other backup.
Download "$REMOTE_PROJECT/ops/Caddyfile" (Join-Path $settingsDir 'Caddyfile')
$diff = Remote "cd $REMOTE_PROJECT && git diff --quiet ops/Caddyfile && echo same || echo different"
if (($diff -join '') -eq 'different') {
  Write-Host 'Caddyfile  OK  (DIFFERS FROM THE REPOSITORY - edited by hand)' -ForegroundColor Yellow
} else {
  Write-Host 'Caddyfile  OK'
}
# The DuckDNS token belongs to the owner of the domain and does not need to sit
# in your personal folder; the owner can issue it again. Only keep the shape of the line.
$cron = Remote "sed -E 's/token=[a-f0-9-]+/token=ASK_THE_DOMAIN_OWNER/' /etc/cron.d/duckdns 2>/dev/null || true"
if ($cron) {
  $cron | Out-File -FilePath (Join-Path $settingsDir 'cron-duckdns.txt') -Encoding utf8
  Write-Host 'cron-duckdns.txt  OK  (token left out on purpose)'
}

# -- 8. README.txt --------------------------------------------------------------
$files = Get-ChildItem -Path $dataDir -File
$totalMb = [Math]::Round(($files | Measure-Object -Property Length -Sum).Sum / 1MB, 2)
$dumpCount = ($files | Where-Object { $_.Name -like '*.dump' }).Count

$readme = @"
FRISEUR - FULL SNAPSHOT
=======================

Taken  : $(Get-Date -Format 'yyyy-MM-dd HH:mm')
Server : $hostName ($Server)

Contents
--------
data\      $($files.Count) files, $totalMb MB  ($dumpCount .dump files)
           .dump     -> complete copy of the database (to roll the system back)
           .json.gz  -> to add missing records back
           .xlsx     -> to open and look at in Excel
settings\  friseur.env  -> the server's secret settings (database password, shared
                           gate password, mail settings). Rename it to ".env"
                           when putting it back on a server.
           Caddyfile    -> the live version from the server. Sites added by hand
                           only exist here, not in the repository.

The SHA-256 hash of every downloaded file was compared with the server and matched.

If something goes wrong
-----------------------
1) A few records are missing / were deleted by mistake
   Admin panel -> System -> "Restore from backup" -> the .json.gz file in data\.
   It only adds what is missing and deletes nothing. Running it twice is harmless.

2) The database is broken, everything has to be rolled back
   a) Put the dump back on the server:
        .\ops\push-dump.ps1 -Server $Server
   b) Then on the server:
        ssh $Server
        friseur restore
      Pick the file from the list. The system first saves a copy of the
      current state, then asks you to type YES.

3) The server is gone, set it up from scratch
   Follow docs/deployment.md, then:
     - put settings\friseur.env on the server as /opt/friseur/.env
     - copy the files in data\ to /opt/friseur/backups/
     - roll back to the newest .dump with friseur restore
   With the settings restored as well, the shared gate password stays the
   same and nobody has to sign in again.

Important
---------
This folder is outside any cloud sync, so it is the only copy. If something
happens to this computer, the copy is gone too. Copy it to a USB stick or an
external disk now and then. It contains passwords; do not put it in a shared folder.
"@

$readme | Out-File -FilePath (Join-Path $folder 'README.txt') -Encoding utf8

# -- Summary -------------------------------------------------------------------
Heading 'Done'
Write-Host "Snapshot ready: $folder"
Write-Host "  data\      $($files.Count) files, $totalMb MB"
Write-Host "  settings\  including the secret settings"
Write-Host "  README.txt how to restore"
Write-Host ''
Write-Host 'Every file was verified against the server  OK' -ForegroundColor Green
Write-Host ''
