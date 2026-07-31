# The Stepwright sign in broker

Atlassian will not let a public client exchange an authorization code. The exchange needs a
client secret, and an application handed out as a file cannot keep one, so without this worker
every company has to register its own Atlassian application and paste an identifier and a
secret into Settings.

With it, a technician presses Sign in to Atlassian and is finished.

## What it is not

It never stores a token beyond the two minutes a sign in takes. It never proxies an API call, so
it cannot be used to reach anybody's Confluence. It only ever hands a sign in back to the
loopback address on the machine that started it. If it is compromised the attacker holds your
client secret, which mints nothing on its own, and rotating it is a change here rather than on
every technician's machine.

## Deploying it

Register one OAuth 2.0 integration in the Atlassian developer console, with the Confluence
permissions and `https://your-worker.workers.dev/callback` as its callback address. Then:

```sh
wrangler kv namespace create SIGNIN     # put the id in wrangler.toml
wrangler secret put ATLASSIAN_CLIENT_ID
wrangler secret put ATLASSIAN_CLIENT_SECRET
wrangler deploy
```

Put the worker address into `Connect.AtlassianBroker` in the app and rebuild. Anyone running
that build signs in with one press.
