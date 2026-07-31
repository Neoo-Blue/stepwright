/**
 * The Stepwright sign in broker.
 *
 * Atlassian will not let a public client exchange an authorization code: the exchange needs a
 * client secret, and an application handed out as a file cannot keep one. So the secret lives
 * here, on a worker the publisher runs, and nothing else about the sign in changes: the person
 * still signs in to Atlassian directly, the consent screen is still Atlassian's, and the tokens
 * still end up on their machine.
 *
 * What this deliberately does not do:
 *   It never stores a token beyond the two minutes a sign in takes.
 *   It never proxies an API call, so it can never be used to reach a customer's Confluence.
 *   It never redirects anywhere except the loopback address on the machine that started it.
 *
 * Deploy with wrangler. It needs one KV namespace bound as SIGNIN, and two secrets:
 *   wrangler secret put ATLASSIAN_CLIENT_ID
 *   wrangler secret put ATLASSIAN_CLIENT_SECRET
 */

const SCOPES = [
  'read:confluence-space.summary',
  'read:confluence-content.all',
  'write:confluence-content',
  'write:confluence-file',
  'offline_access',
].join(' ')

/** The only place a sign in may be handed back to: this machine, on the agreed port. */
const ALLOWED_PORTS = [53682]

const json = (body, status = 200) =>
  new Response(JSON.stringify(body), {
    status,
    headers: { 'content-type': 'application/json', 'cache-control': 'no-store' },
  })

const page = (message) =>
  new Response(
    `<!doctype html><html><head><meta charset="utf-8"><title>Stepwright</title></head>` +
      `<body style="font-family:Segoe UI,Helvetica,Arial,sans-serif;padding:48px;">` +
      `<h2>Stepwright</h2><p>${message}</p></body></html>`,
    { status: 200, headers: { 'content-type': 'text/html; charset=utf-8', 'cache-control': 'no-store' } },
  )

async function exchange(env, form) {
  const response = await fetch('https://auth.atlassian.com/oauth/token', {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({
      client_id: env.ATLASSIAN_CLIENT_ID,
      client_secret: env.ATLASSIAN_CLIENT_SECRET,
      ...form,
    }),
  })

  const body = await response.json().catch(() => ({}))
  return { ok: response.ok, body }
}

export default {
  async fetch(request, env) {
    const url = new URL(request.url)
    const self = url.origin

    // 1. The application starts a sign in and says which loopback port it is listening on.
    if (url.pathname === '/start' && request.method === 'GET') {
      const port = Number(url.searchParams.get('port') || 0)
      const state = url.searchParams.get('state') || ''

      if (!ALLOWED_PORTS.includes(port) || state.length < 16) {
        return page('That sign in request was not valid.')
      }

      // The state is remembered here rather than trusted from the browser later.
      await env.SIGNIN.put(`state:${state}`, String(port), { expirationTtl: 300 })

      const authorize = new URL('https://auth.atlassian.com/authorize')
      authorize.searchParams.set('audience', 'api.atlassian.com')
      authorize.searchParams.set('client_id', env.ATLASSIAN_CLIENT_ID)
      authorize.searchParams.set('scope', SCOPES)
      authorize.searchParams.set('redirect_uri', `${self}/callback`)
      authorize.searchParams.set('state', state)
      authorize.searchParams.set('response_type', 'code')
      authorize.searchParams.set('prompt', 'consent')

      return Response.redirect(authorize.toString(), 302)
    }

    // 2. Atlassian comes back here, because only here can the code be exchanged.
    if (url.pathname === '/callback' && request.method === 'GET') {
      const state = url.searchParams.get('state') || ''
      const code = url.searchParams.get('code') || ''
      const failure = url.searchParams.get('error_description') || url.searchParams.get('error')

      const port = await env.SIGNIN.get(`state:${state}`)

      if (!port) {
        return page('That sign in has expired. Start it again from Stepwright.')
      }

      await env.SIGNIN.delete(`state:${state}`)

      if (failure) {
        return page(`Atlassian said no. ${failure}`)
      }

      const { ok, body } = await exchange(env, {
        grant_type: 'authorization_code',
        code,
        redirect_uri: `${self}/callback`,
      })

      if (!ok || !body.access_token) {
        return page('The sign in could not be completed.')
      }

      // The tokens are handed over by ticket rather than in the address, so they never appear
      // in browser history, and the ticket is good once and only for two minutes.
      const ticket = crypto.randomUUID() + crypto.randomUUID()
      await env.SIGNIN.put(`ticket:${ticket}`, JSON.stringify(body), { expirationTtl: 120 })

      return Response.redirect(`http://localhost:${port}/callback?ticket=${ticket}`, 302)
    }

    // 3. The application claims its tokens directly, over its own connection.
    if (url.pathname === '/claim' && request.method === 'POST') {
      const asked = await request.json().catch(() => ({}))
      const ticket = String(asked.ticket || '')

      if (!ticket) {
        return json({ error: 'no ticket' }, 400)
      }

      const held = await env.SIGNIN.get(`ticket:${ticket}`)

      if (!held) {
        return json({ error: 'that ticket has been used or has expired' }, 404)
      }

      await env.SIGNIN.delete(`ticket:${ticket}`)
      return json(JSON.parse(held))
    }

    // 4. Renewal also needs the secret, so it also happens here. Nothing is kept.
    if (url.pathname === '/refresh' && request.method === 'POST') {
      const asked = await request.json().catch(() => ({}))
      const refresh = String(asked.refresh_token || '')

      if (!refresh) {
        return json({ error: 'no refresh token' }, 400)
      }

      const { ok, body } = await exchange(env, {
        grant_type: 'refresh_token',
        refresh_token: refresh,
      })

      return json(body, ok ? 200 : 400)
    }

    return page('Nothing to see here.')
  },
}
