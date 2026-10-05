<#
  Friseur - puts a backup from your local snapshot back on the server.

  Usage (PowerShell):
      .\ops\push-dump.ps1 -Server root@SERVER_IP
      .\ops\push-dump.ps1 -Server root@SERVER_IP -Source C:\Friseur-Snapshots\2026-09-05-0158
      .\ops\push-dump.ps1 -Server root@SERVER_IP -File C:\...\data\friseur-2026-09-05.dump

  This script only MOVES and verifies; it does not touch the database. The
  destructive step is "friseur restore" on the server, which has its own
  safeguards: it saves a copy of the current state first, makes you type YES
  in capitals, then stops and restarts the app. Writing a second copy of those
  safeguards here would only duplicate them.
#>
param(
  [Parameter(Mandatory = $true)]
  [string]$Server,
  # Snapshot folder to pick the file from (as produced by pull-snapshot.ps1)
  [string]$Source = 'C:\Friseur-Snapshots',
  # Given a .dump path directly, no questions are asked
  [string]$File
)

$ErrorActionPreference = 'Stop'

$SSH_OPT = @('-o', 'ConnectTimeout=20', '-o', 'BatchMode=yes')
$REMOTE_BACKUPS = '/opt/friseur/backups'

function Heading($text) {
  Write-Host ''
  Write-Host ("-- " + $text + " " + ("-" * [Math]::Max(0, 46 - $text.Length)))
}

function Remote([string]$Command) {
  $output = & ssh @SSH_OPT $Server $Command
  if ($LASTEXITCODE -ne 0) { throw "Command failed on the server (exit $LASTEXITCODE): $Command" }
  return $output
}

# -- 1. Which file --------------------------------------------------------------
if ($File) {
  if (-not (Test-Path $File)) { throw "File not found: $File" }
  $chosen = Get-Item $File
} else {
  if (-not (Test-Path $Source)) { throw "Snapshot folder not found: $Source" }
  $candidates = Get-ChildItem -Path $Source -Filter '*.dump' -Recurse -File |
    Sort-Object LastWriteTime -Descending
  if ($candidates.Count -eq 0) { throw "No .dump files in $Source." }

  Heading 'Your backups (newest first)'
  for ($i = 0; $i -lt [Math]::Min($candidates.Count, 15); $i++) {
    $d = $candidates[$i]
    '{0,3}) {1,-32} {2,8:N0} KB   {3}' -f ($i + 1), $d.Name, ($d.Length / 1KB), $d.LastWriteTime.ToString('yyyy-MM-dd')
  }
  Write-Host ''
  $answer = Read-Host 'Which backup should go to the server? (number, Enter to cancel)'
  if ([string]::IsNullOrWhiteSpace($answer)) { Write-Host 'Cancelled.'; exit 0 }
  $n = 0
  if (-not [int]::TryParse($answer, [ref]$n)) { throw "Invalid choice: $answer" }
  if ($n -lt 1 -or $n -gt $candidates.Count) { throw "There is no line $n in the list." }
  $chosen = $candidates[$n - 1]
}

Heading 'Chosen'
Write-Host "$($chosen.Name)  ($([Math]::Round($chosen.Length / 1KB)) KB, $($chosen.LastWriteTime.ToString('yyyy-MM-dd HH:mm')))"

# -- 2. Upload ------------------------------------------------------------------
Heading 'Uploading to the server'
& scp @SSH_OPT $chosen.FullName ($Server + ':' + $REMOTE_BACKUPS + '/')
if ($LASTEXITCODE -ne 0) { throw 'Upload failed.' }

# -- 3. Verify ------------------------------------------------------------------
# Restoring a half-uploaded dump is the way to end up with an empty database.
Heading 'Verifying'
$localHash = (Get-FileHash -Algorithm SHA256 -Path $chosen.FullName).Hash.ToLower()
$remoteHash = ((Remote "sha256sum '$REMOTE_BACKUPS/$($chosen.Name)'") -join '').Split(' ')[0]
if ($localHash -ne $remoteHash) {
  Write-Host 'The file on the server is NOT THE SAME as yours.' -ForegroundColor Red
  throw 'Upload could not be verified; do not attempt the restore.'
}
Write-Host 'The file on the server is identical to yours  OK'

# Ask postgres itself whether the file is intact
Remote "cd /opt/friseur && docker compose exec -T db pg_restore --list '/backups/$($chosen.Name)' > /dev/null" | Out-Null
Write-Host 'The file opens  OK'

# -- 4. What's next -------------------------------------------------------------
Heading 'Next'
Write-Host 'The file is ready on the server. To roll the database back:'
Write-Host ''
Write-Host "    ssh $Server"
Write-Host '    friseur restore'
Write-Host ''
Write-Host "Pick $($chosen.Name) from the list."
Write-Host 'The system saves a copy of the current state first, then asks you to type YES.'
Write-Host ''
Write-Host 'Careful: records entered AFTER that backup are lost. If only a few' -ForegroundColor Yellow
Write-Host 'records are missing, restore the .json.gz from the admin panel instead;' -ForegroundColor Yellow
Write-Host 'it only adds what is missing and deletes nothing.' -ForegroundColor Yellow
Write-Host ''
