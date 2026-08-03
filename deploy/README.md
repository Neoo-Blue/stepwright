# Handing Stepwright to a company

Twenty technicians should not each be pasting a key in. Run one script on a machine and every
person on it starts with the same settings, filled in and, if you want, not theirs to change.

## Setting it

Run as an administrator:

```powershell
.\Set-StepwrightPolicy.ps1 -SetBy "Contoso IT" `
    -AiProvider openai -AiKey "sk-..." `
    -HuduBaseUrl "https://help.contoso.com" -HuduKey "abcd..."
```

That writes `C:\ProgramData\Stepwright\policy.json`, which everyone may read and only
administrators may write. Stepwright reads it at startup and lays it over whatever the person had.

Add `-Unlocked` to fill the values in as a starting point that people may still change. Without it
they are fixed: shown, greyed out, and left alone.

`-Remove` takes the policy away again, and everyone keeps their own settings from then on.

Through Intune, Group Policy, an RMM or anything else that can run a script as SYSTEM: run the same
command. There is nothing else to install.

## What a person sees

The settings page fills in what you set and greys it out, and says along the bottom who set it. A
key you sealed is not shown at all: the box says `Set by Contoso IT, and not shown here`. It is not
copied into the person's own settings file either, so it is not in their profile to be found.

## What the sealing does, and what it does not

A key is sealed to the machine before it is written, so the file is worth nothing if it is copied
to another machine, and it cannot be read out of the file or off the screen.

It is not armour against somebody with administrator rights on their own machine who sets out to
pull the key from a running program. Nothing that leaves a usable key on a machine can be, in any
application, and anybody who tells you otherwise is selling something.

If a key must never be recoverable by the person holding it, do not put it on their machine. Use a
route where they sign in as themselves instead:

```powershell
.\Set-StepwrightPolicy.ps1 -SetBy "Contoso IT" -AiProvider copilot -AiAuth browser
```

Now the company fixes which assistant is used, every technician signs in to Copilot as themselves,
their own licence pays for it, and there is no key on any machine at all. The same is true of the
Claude subscription sign in and the Confluence sign in.

## Everything you can set

| What | Switch |
| ---- | ------ |
| Your name, shown to the person | `-SetBy` |
| Which assistant | `-AiProvider` (openai, anthropic, gemini, copilot, foundry, custom) |
| How it signs in | `-AiAuth` (key, cli, token, subscription, microsoft, browser) |
| Address and model | `-AiBaseUrl`, `-AiModel` |
| The key, sealed | `-AiKey` |
| Microsoft application and tenant | `-AiAppId`, `-AiTenant` |
| Hudu site and key, sealed | `-HuduBaseUrl`, `-HuduKey` |
| How Hudu publishes | `-HuduPublish` (key, web) |
| Confluence site, account, token | `-ConfluenceSite`, `-ConfluenceEmail`, `-ConfluenceToken` |
| How Confluence signs in | `-ConfluenceAuth` (token, oauth) |
| Where guides are kept | `-LibraryFolder` |
| Let people change it after all | `-Unlocked` |
| Take the policy away | `-Remove` |

## Licensing

Stepwright is free for noncommercial use. Handing it to technicians at a company is commercial
use, and needs permission first. It is often given freely. Write to aierfate.aierken@gmail.com
with who you are, what you would use it for, and roughly how many people. See LICENSE.
