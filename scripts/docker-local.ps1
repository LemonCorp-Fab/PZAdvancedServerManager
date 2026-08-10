[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('setup', 'build', 'up', 'down', 'logs', 'check', 'config')]
    [string]$Action = 'up'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$isWindowsPlatform = $PSVersionTable.PSEdition -eq 'Desktop' -or
    [Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([Runtime.InteropServices.OSPlatform]::Windows)
if (-not $isWindowsPlatform) {
    throw 'The DPAPI Docker wrapper is available only on Windows. Use a protected .env file on Linux.'
}

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$localData = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
$secretRoot = Join-Path $localData 'LemonCorp\PZAdvancedServerManager\secrets'
$administratorSecretPath = Join-Path $secretRoot 'docker-admin-password.dpapi'
$dataEncryptionSecretPath = Join-Path $secretRoot 'docker-data-encryption-key.dpapi'

function Resolve-DockerExecutable {
    $command = Get-Command docker.exe -ErrorAction SilentlyContinue
    if ($null -ne $command) { return $command.Source }

    $candidates = @(
        (Join-Path $env:LOCALAPPDATA 'Programs\DockerDesktop\resources\bin\docker.exe'),
        'C:\Program Files\Docker\Docker\resources\bin\docker.exe'
    )
    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) { return $candidate }
    }
    throw 'Docker Desktop is not installed or docker.exe cannot be found.'
}

function ConvertTo-PlainText([Security.SecureString]$SecureValue) {
    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($SecureValue)
    try { return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer) }
}

function Protect-SecretFile([string]$Path) {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $system = [Security.Principal.SecurityIdentifier]::new([Security.Principal.WellKnownSidType]::LocalSystemSid, $null)
    $acl = New-Object Security.AccessControl.FileSecurity
    $acl.SetOwner($identity.User)
    $acl.SetAccessRuleProtection($true, $false)
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($identity.User, 'FullControl', 'Allow'))
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($system, 'FullControl', 'Allow'))
    Set-Acl -LiteralPath $Path -AclObject $acl
}

function Save-EncryptedSecret([Security.SecureString]$Value, [string]$Path) {
    [IO.Directory]::CreateDirectory($secretRoot) | Out-Null
    $encrypted = ConvertFrom-SecureString -SecureString $Value
    [IO.File]::WriteAllText($Path, $encrypted, [Text.UTF8Encoding]::new($false))
    Protect-SecretFile -Path $Path
}

function Save-AdministratorSecret {
    $password = Read-Host 'Docker administrator password' -AsSecureString
    $confirmation = Read-Host 'Confirm the Docker administrator password' -AsSecureString
    $plainPassword = ConvertTo-PlainText $password
    $plainConfirmation = ConvertTo-PlainText $confirmation
    try {
        if ($plainPassword -cne $plainConfirmation) { throw 'The passwords do not match.' }
        if ($plainPassword.Length -lt 12 -or
            $plainPassword -cnotmatch '[A-Z]' -or
            $plainPassword -cnotmatch '[a-z]' -or
            $plainPassword -notmatch '[0-9]') {
            throw 'Use at least 12 characters with an uppercase letter, a lowercase letter, and a digit.'
        }

        Save-EncryptedSecret -Value $password -Path $administratorSecretPath
        if (-not (Test-Path -LiteralPath $dataEncryptionSecretPath)) { Save-DataEncryptionSecret }
        Write-Host "Encrypted Docker secrets saved for the current Windows account: $secretRoot"
    }
    finally {
        $plainPassword = $null
        $plainConfirmation = $null
    }
}

function Save-DataEncryptionSecret {
    $bytes = [byte[]]::new(32)
    $generator = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $generator.GetBytes($bytes) }
    finally { $generator.Dispose() }
    $plainKey = [Convert]::ToBase64String($bytes)
    [Array]::Clear($bytes, 0, $bytes.Length)
    try {
        $secureKey = ConvertTo-SecureString $plainKey -AsPlainText -Force
        Save-EncryptedSecret -Value $secureKey -Path $dataEncryptionSecretPath
    }
    finally { $plainKey = $null }
}

function Invoke-DockerCompose([string[]]$Arguments) {
    & $script:dockerExecutable compose -f compose.yaml -f compose.local.yaml @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Docker Compose failed with exit code $LASTEXITCODE." }
}

if ($Action -eq 'setup') {
    Save-AdministratorSecret
    exit 0
}

if (-not (Test-Path -LiteralPath $administratorSecretPath)) {
    throw "No encrypted local Docker secret exists. Run 'just docker-secret-setup' first."
}
if (-not (Test-Path -LiteralPath $dataEncryptionSecretPath)) { Save-DataEncryptionSecret }

$dockerExecutable = Resolve-DockerExecutable
$dockerDirectory = Split-Path -Parent $dockerExecutable
$encryptedSecret = [IO.File]::ReadAllText($administratorSecretPath, [Text.Encoding]::UTF8).Trim()
$secureSecret = ConvertTo-SecureString $encryptedSecret
$plainSecret = ConvertTo-PlainText $secureSecret
$encryptedDataKey = [IO.File]::ReadAllText($dataEncryptionSecretPath, [Text.Encoding]::UTF8).Trim()
$secureDataKey = ConvertTo-SecureString $encryptedDataKey
$plainDataKey = ConvertTo-PlainText $secureDataKey
$previousSecret = [Environment]::GetEnvironmentVariable('PZASM_ADMIN_PASSWORD', 'Process')
$previousDataKey = [Environment]::GetEnvironmentVariable('PZASM_DATA_ENCRYPTION_KEY', 'Process')
$previousPath = [Environment]::GetEnvironmentVariable('PATH', 'Process')

Push-Location $repositoryRoot
try {
    [Environment]::SetEnvironmentVariable('PZASM_ADMIN_PASSWORD', $plainSecret, 'Process')
    [Environment]::SetEnvironmentVariable('PZASM_DATA_ENCRYPTION_KEY', $plainDataKey, 'Process')
    if (($previousPath -split ';') -notcontains $dockerDirectory) {
        [Environment]::SetEnvironmentVariable('PATH', "$dockerDirectory;$previousPath", 'Process')
    }
    switch ($Action) {
        'build' { Invoke-DockerCompose -Arguments @('build') }
        'up' { Invoke-DockerCompose -Arguments @('up', '--detach', '--build') }
        'down' { Invoke-DockerCompose -Arguments @('down') }
        'logs' { Invoke-DockerCompose -Arguments @('logs', '--follow', 'manager') }
        'config' { Invoke-DockerCompose -Arguments @('config') }
        'check' {
            Invoke-DockerCompose -Arguments @('config', '--quiet')
            Invoke-DockerCompose -Arguments @('build')
        }
    }
}
finally {
    [Environment]::SetEnvironmentVariable('PZASM_ADMIN_PASSWORD', $previousSecret, 'Process')
    [Environment]::SetEnvironmentVariable('PZASM_DATA_ENCRYPTION_KEY', $previousDataKey, 'Process')
    [Environment]::SetEnvironmentVariable('PATH', $previousPath, 'Process')
    $plainSecret = $null
    $plainDataKey = $null
    Pop-Location
}
