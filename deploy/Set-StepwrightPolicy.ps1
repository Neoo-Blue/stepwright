<#
.SYNOPSIS
    Sets Stepwright up for every person on this machine, once, from a script.

.DESCRIPTION
    Writes the machine wide settings Stepwright reads at startup. Anything set here is filled in
    for every technician who uses the machine, and unless -Unlocked is given it is fixed: the
    fields are greyed out in Settings and cannot be changed.

    Keys are sealed to this machine before they are written, and Stepwright never shows them. The
    settings page says who set the key rather than what it is, and the key is never copied into
    the person's own settings file.

    Be honest with yourself about what that does and does not do. It stops a technician changing
    what they should not change and stops the key being read off a screen or out of a file. It is
    not armour against somebody who has administrator rights on the machine and sets out to pull
    the key from a running program. Nothing that leaves a usable key on a machine can be. If a key
    must never be recoverable by the person holding it, do not put it on their machine: give them
    a sign in route instead, which is what the Copilot, Claude and Confluence sign ins are for.

.PARAMETER SetBy
    The name of your company, shown to the person on the settings page.

.PARAMETER Unlocked
    Fill the values in as a starting point but let people change them.

.EXAMPLE
    .\Set-StepwrightPolicy.ps1 -SetBy "Contoso IT" -AiProvider openai -AiKey "sk-..." `
        -HuduBaseUrl "https://help.contoso.com" -HuduKey "abcd..."

.EXAMPLE
    .\Set-StepwrightPolicy.ps1 -SetBy "Contoso IT" -AiProvider copilot -AiAuth browser
    Nothing to seal at all: every technician signs in to Copilot themselves, and the company only
    fixes which assistant is used.
#>

[CmdletBinding()]
param(
    [string] $SetBy,

    [ValidateSet('openai', 'anthropic', 'gemini', 'copilot', 'foundry', 'custom')]
    [string] $AiProvider,

    [ValidateSet('key', 'cli', 'token', 'subscription', 'microsoft', 'browser')]
    [string] $AiAuth,

    [string] $AiBaseUrl,
    [string] $AiModel,
    [string] $AiKey,
    [string] $AiAppId,
    [string] $AiTenant,

    [string] $HuduBaseUrl,
    [string] $HuduKey,

    [ValidateSet('key', 'web')]
    [string] $HuduPublish,

    [string] $ConfluenceSite,
    [string] $ConfluenceEmail,
    [string] $ConfluenceToken,

    [ValidateSet('token', 'oauth')]
    [string] $ConfluenceAuth,

    [string] $LibraryFolder,

    [switch] $Unlocked,
    [switch] $Remove
)

$ErrorActionPreference = 'Stop'

function Test-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-Administrator)) {
    throw "Run this as an administrator. It writes to a folder ordinary accounts may only read."
}

$folder = Join-Path $env:ProgramData 'Stepwright'
$file = Join-Path $folder 'policy.json'

if ($Remove) {
    if (Test-Path $file) {
        Remove-Item $file -Force
        Write-Host "Removed $file. Everyone keeps their own settings from now on."
    }
    else {
        Write-Host "There was no policy to remove."
    }
    return
}

# Seals a secret so it can be read on this machine and nowhere else. The machine scope is used
# rather than the user scope on purpose: the account running this script and the account using
# Stepwright are not the same, and a secret sealed to one account would be unreadable by the other.
Add-Type -AssemblyName System.Security
function Protect-Secret {
    param([string] $Plain)

    if ([string]::IsNullOrWhiteSpace($Plain)) { return $null }

    $salt = [Text.Encoding]::UTF8.GetBytes('Stepwright policy 1')
    $bytes = [Text.Encoding]::UTF8.GetBytes($Plain)

    $sealed = [Security.Cryptography.ProtectedData]::Protect(
        $bytes, $salt, [Security.Cryptography.DataProtectionScope]::LocalMachine)

    return [Convert]::ToBase64String($sealed)
}

New-Item -ItemType Directory -Force -Path $folder | Out-Null

$policy = [ordered] @{}

function Add-Value {
    param([string] $Name, [string] $Value)
    if (-not [string]::IsNullOrWhiteSpace($Value)) { $policy[$Name] = $Value.Trim() }
}

Add-Value 'setBy'            $SetBy
Add-Value 'aiProvider'       $AiProvider
Add-Value 'aiAuth'           $AiAuth
Add-Value 'aiBaseUrl'        $AiBaseUrl
Add-Value 'aiModel'          $AiModel
Add-Value 'aiAppId'          $AiAppId
Add-Value 'aiTenant'         $AiTenant
Add-Value 'huduBaseUrl'      $HuduBaseUrl
Add-Value 'huduPublish'      $HuduPublish
Add-Value 'confluenceSite'   $ConfluenceSite
Add-Value 'confluenceEmail'  $ConfluenceEmail
Add-Value 'confluenceAuth'   $ConfluenceAuth
Add-Value 'libraryFolder'    $LibraryFolder

Add-Value 'aiKeyProtected'            (Protect-Secret $AiKey)
Add-Value 'huduKeyProtected'          (Protect-Secret $HuduKey)
Add-Value 'confluenceTokenProtected'  (Protect-Secret $ConfluenceToken)

$policy['locked'] = -not $Unlocked.IsPresent

$policy | ConvertTo-Json -Depth 4 | Set-Content -Path $file -Encoding UTF8

# Everyone may read it, because Stepwright runs as the person and has to. Only administrators and
# the system may write it, because that is the whole point of setting it here rather than there.
$acl = Get-Acl $file
$acl.SetAccessRuleProtection($true, $false)
$acl.Access | ForEach-Object { $acl.RemoveAccessRule($_) | Out-Null }

$rules = @(
    @('BUILTIN\Administrators', 'FullControl'),
    @('NT AUTHORITY\SYSTEM',    'FullControl'),
    @('BUILTIN\Users',          'Read')
)

foreach ($rule in $rules) {
    $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule(
        $rule[0], $rule[1], 'Allow')))
}

Set-Acl -Path $file -AclObject $acl

Write-Host "Stepwright policy written to $file"
Write-Host ("Settings fixed: " + (($policy.Keys | Where-Object { $_ -ne 'locked' }) -join ', '))

if ($policy['locked']) {
    Write-Host "These are locked. People will see them filled in and greyed out."
}
else {
    Write-Host "These are a starting point only. People may change them."
}

foreach ($sealed in 'aiKeyProtected', 'huduKeyProtected', 'confluenceTokenProtected') {
    if ($policy.Contains($sealed)) {
        Write-Host "A secret was sealed to this machine for $sealed. Stepwright will use it and never show it."
    }
}
