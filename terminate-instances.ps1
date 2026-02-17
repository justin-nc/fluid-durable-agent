# Script to terminate all running orchestration instances
# This resolves NonDeterministicOrchestrationException by clearing old instances

Write-Host "This will terminate all running orchestration instances in local storage." -ForegroundColor Yellow
Write-Host "This is safe for development but will clear all active sessions." -ForegroundColor Yellow
Write-Host ""
$confirm = Read-Host "Continue? (y/n)"

if ($confirm -ne 'y') {
    Write-Host "Cancelled." -ForegroundColor Red
    exit
}

# For local development using Azurite, we can clear the storage
$storageFiles = @(
    "__azurite_db_blob__.json",
    "__azurite_db_blob_extent__.json"
)

$backupFolder = "azurite_backup_$(Get-Date -Format 'yyyyMMdd_HHmmss')"
New-Item -ItemType Directory -Path $backupFolder -Force | Out-Null

foreach ($file in $storageFiles) {
    if (Test-Path $file) {
        Copy-Item $file "$backupFolder\$file" -Force
        Remove-Item $file -Force
        Write-Host "Backed up and removed: $file" -ForegroundColor Green
    }
}

# Clear blob storage folder
if (Test-Path "__blobstorage__") {
    Move-Item "__blobstorage__" "$backupFolder\__blobstorage__" -Force
    Write-Host "Backed up and removed: __blobstorage__" -ForegroundColor Green
}

Write-Host "`nAll orchestration instances have been cleared." -ForegroundColor Green
Write-Host "Backup saved to: $backupFolder" -ForegroundColor Cyan
Write-Host "`nYou can now restart your functions host." -ForegroundColor Yellow
