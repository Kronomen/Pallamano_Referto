/**
 * Referto Pallamano - Cloudflare Worker API
 * Versione con accessi amministratore/operatori e dispositivi revocabili.
 * Binding D1 richiesto: DB -> pallamano-operatori-test
 * Secrets richiesti: GITHUB_TOKEN, APP_PASSWORD, ADMIN_JWT_SECRET
 */
const OWNER = 'Kronomen';
const REPO = 'Pallamano_Referto_Dati_Test';
const BRANCH = 'main';
const ALLOWED_ORIGINS = new Set(['https://kronomen.github.io', 'http://localhost:8088']);
const ADMIN_TOKEN_TTL_SECONDS = 8 * 60 * 60;
const MAX_BODY_BYTES = 2 * 1024 * 1024;
const encoder = new TextEncoder();
const decoder = new TextDecoder();

function corsHeaders(origin) {
  return {
    'Access-Control-Allow-Origin': ALLOWED_ORIGINS.has(origin) ? origin : 'null',
    'Access-Control-Allow-Methods': 'GET, PUT, POST, PATCH, OPTIONS',
    'Access-Control-Allow-Headers': 'Content-Type, Authorization',
    'Access-Control-Max-Age': '86400',
    'Vary': 'Origin'
  };
}
function jsonResponse(data, status, origin, extra = {}) {
  return new Response(JSON.stringify(data), {
    status,
    headers: { 'Content-Type': 'application/json; charset=utf-8', 'Cache-Control': 'no-store', ...corsHeaders(origin), ...extra }
  });
}
function base64UrlEncode(bytes) {
  let binary = '';
  for (const byte of bytes) binary += String.fromCharCode(byte);
  return btoa(binary).replace(/=/g, '').replace(/\+/g, '-').replace(/\//g, '_');
}
function base64UrlDecodeToBytes(value) {
  const normalized = value.replace(/-/g, '+').replace(/_/g, '/');
  return Uint8Array.from(atob(normalized + '='.repeat((4 - normalized.length % 4) % 4)), c => c.charCodeAt(0));
}
async function hmacKey(secret) {
  return crypto.subtle.importKey('raw', encoder.encode(secret), { name: 'HMAC', hash: 'SHA-256' }, false, ['sign', 'verify']);
}
async function issueAdminJwt(secret) {
  const now = Math.floor(Date.now() / 1000);
  const head = base64UrlEncode(encoder.encode(JSON.stringify({ alg: 'HS256', typ: 'JWT' })));
  const payload = base64UrlEncode(encoder.encode(JSON.stringify({ sub: 'referto-admin', iat: now, exp: now + ADMIN_TOKEN_TTL_SECONDS })));
  const unsigned = head + '.' + payload;
  const signature = new Uint8Array(await crypto.subtle.sign('HMAC', await hmacKey(secret), encoder.encode(unsigned)));
  return unsigned + '.' + base64UrlEncode(signature);
}
async function verifyAdminJwt(token, secret) {
  const parts = token.split('.');
  if (parts.length !== 3) return false;
  try {
    const payload = JSON.parse(decoder.decode(base64UrlDecodeToBytes(parts[1])));
    if (payload.sub !== 'referto-admin' || !Number.isInteger(payload.exp) || payload.exp <= Math.floor(Date.now() / 1000)) return false;
    return await crypto.subtle.verify('HMAC', await hmacKey(secret), base64UrlDecodeToBytes(parts[2]), encoder.encode(parts[0] + '.' + parts[1]));
  } catch { return false; }
}
function constantTimeEqual(a, b) {
  const aa = encoder.encode(a), bb = encoder.encode(b);
  let diff = aa.length ^ bb.length;
  for (let i = 0; i < Math.max(aa.length, bb.length); i++) diff |= (aa[i] || 0) ^ (bb[i] || 0);
  return diff === 0;
}
async function sha256Hex(value) {
  const digest = new Uint8Array(await crypto.subtle.digest('SHA-256', encoder.encode(value)));
  return Array.from(digest, b => b.toString(16).padStart(2, '0')).join('');
}
function randomSecret(bytes = 32) {
  const data = crypto.getRandomValues(new Uint8Array(bytes));
  return base64UrlEncode(data);
}
function makeId(prefix) { return prefix + '_' + randomSecret(12); }

async function readRequestJson(request) {
  const declared = Number(request.headers.get('Content-Length') || 0);
  if (declared > MAX_BODY_BYTES) throw new Error('Richiesta troppo grande.');
  const text = await request.text();
  if (encoder.encode(text).length > MAX_BODY_BYTES) throw new Error('Richiesta troppo grande.');
  try { return JSON.parse(text); } catch { throw new Error('JSON della richiesta non valido.'); }
}
function decodeGitHubContent(content) {
  const binary = atob(content.replace(/\s/g, ''));
  return decoder.decode(Uint8Array.from(binary, c => c.charCodeAt(0)));
}
function encodeGitHubContent(text) {
  const bytes = encoder.encode(text);
  let binary = '';
  for (let i = 0; i < bytes.length; i += 0x8000) binary += String.fromCharCode(...bytes.subarray(i, i + 0x8000));
  return btoa(binary);
}
async function githubRequest(path, token, options = {}) {
  return fetch(`https://api.github.com/repos/${OWNER}/${REPO}/contents/${path}`, {
    ...options,
    headers: {
      'Accept': 'application/vnd.github+json', 'Authorization': `Bearer ${token}`,
      'X-GitHub-Api-Version': '2022-11-28', 'User-Agent': 'Pallamano-Referto-Cloudflare-Worker',
      ...(options.headers || {})
    }
  });
}
const FILES = {
  archive: 'Archivio_Gestionale/archivio_atleti.json',
  index: 'Indice_Partite/elenco_partite.json'
};
function matchPath(id) {
  if (!/^[A-Za-z0-9_-]{1,80}$/.test(id)) return null;
  return `Partite/${id}.json`;
}
async function readFile(path, token) {
  const response = await githubRequest(`${path}?ref=${BRANCH}`, token);
  if (response.status === 404) return { exists: false, sha: null, data: null };
  if (!response.ok) throw new Error(`GitHub GET ${response.status}: ${await response.text()}`);
  const file = await response.json();
  if (file.encoding !== 'base64' || typeof file.content !== 'string') throw new Error('Formato file GitHub non supportato.');
  const text = decodeGitHubContent(file.content);
  let data;
  try { data = JSON.parse(text); } catch { throw new Error(`Il file ${path} nel repository non contiene JSON valido.`); }
  return { exists: true, sha: file.sha, data };
}
async function writeFile(path, data, expectedSha, token) {
  const current = await readFile(path, token);
  if (current.exists && (!expectedSha || expectedSha !== current.sha)) return { conflict: true, currentSha: current.sha };
  if (!current.exists && expectedSha) return { conflict: true, currentSha: null };
  const payload = { message: `Aggiornamento archivio Referto Pallamano: ${path}`, content: encodeGitHubContent(JSON.stringify(data, null, 2)), branch: BRANCH };
  if (current.exists) payload.sha = current.sha;
  const response = await githubRequest(path, token, { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(payload) });
  if (response.status === 409 || response.status === 422) return { conflict: true, currentSha: current.sha };
  if (!response.ok) throw new Error(`GitHub PUT ${response.status}: ${await response.text()}`);
  const result = await response.json();
  return { conflict: false, sha: result.content.sha, commit: result.commit.sha };
}

async function rateLimitState(env, key, limit = 8, windowSeconds = 900) {
  const now = Math.floor(Date.now() / 1000);
  const row = await env.DB.prepare(
    'SELECT attempts, window_started_at, blocked_until FROM access_rate_limits WHERE rate_key = ?'
  ).bind(key).first();
  if (row?.blocked_until && row.blocked_until > now) {
    return { blocked: true, retryAfter: row.blocked_until - now };
  }
  if (row?.window_started_at && now - row.window_started_at < windowSeconds && row.attempts >= limit) {
    return { blocked: true, retryAfter: windowSeconds - (now - row.window_started_at) };
  }
  return { blocked: false, retryAfter: 0 };
}
async function recordFailedAttempt(env, key, limit = 8, windowSeconds = 900) {
  const now = Math.floor(Date.now() / 1000);
  const resetBefore = now - windowSeconds;
  const blockedUntil = now + windowSeconds;
  const row = await env.DB.prepare(
    `INSERT INTO access_rate_limits (rate_key, attempts, window_started_at, blocked_until)
     VALUES (?, 1, ?, NULL)
     ON CONFLICT(rate_key) DO UPDATE SET
       attempts = CASE WHEN access_rate_limits.window_started_at <= ? THEN 1 ELSE access_rate_limits.attempts + 1 END,
       window_started_at = CASE WHEN access_rate_limits.window_started_at <= ? THEN excluded.window_started_at ELSE access_rate_limits.window_started_at END,
       blocked_until = CASE
         WHEN access_rate_limits.window_started_at <= ? THEN NULL
         WHEN access_rate_limits.attempts + 1 >= ? THEN ?
         ELSE access_rate_limits.blocked_until
       END
     RETURNING attempts, blocked_until`
  ).bind(key, now, resetBefore, resetBefore, resetBefore, limit, blockedUntil).first();
  return { blocked: Number(row?.attempts || 0) >= limit, retryAfter: row?.blocked_until ? windowSeconds : 0 };
}
async function clearRateLimit(env, key) {
  await env.DB.prepare('DELETE FROM access_rate_limits WHERE rate_key = ?').bind(key).run();
}
async function rateKey(request, purpose) {
  const ip = request.headers.get('CF-Connecting-IP') || 'unknown';
  return purpose + ':' + await sha256Hex(ip);
}

async function authenticate(request, env) {
  const header = request.headers.get('Authorization') || '';
  const token = header.startsWith('Bearer ') ? header.slice(7) : '';
  if (!token) return null;
  if (await verifyAdminJwt(token, env.ADMIN_JWT_SECRET)) return { role: 'admin' };
  if (!env.DB) return null;
  const tokenHash = await sha256Hex(token);
  const row = await env.DB.prepare(
    `SELECT d.id AS device_id, d.operator_id, o.name AS operator_name
     FROM devices d JOIN operators o ON o.id = d.operator_id
     WHERE d.token_hash = ? AND d.revoked_at IS NULL AND o.active = 1 LIMIT 1`
  ).bind(tokenHash).first();
  if (!row) return null;
  await env.DB.prepare('UPDATE devices SET last_seen_at = CURRENT_TIMESTAMP WHERE id = ?').bind(row.device_id).run();
  return { role: 'operator', deviceId: row.device_id, operatorId: row.operator_id, operatorName: row.operator_name };
}
function requireAdmin(auth) { return auth?.role === 'admin'; }

async function createOperator(env, body) {
  const name = typeof body.name === 'string' ? body.name.trim() : '';
  if (!name || name.length > 100) throw new Error('Inserisci un nominativo valido (massimo 100 caratteri).');
  const code = 'PH-' + randomSecret(18).toUpperCase();
  const id = makeId('op');
  await env.DB.prepare(
    'INSERT INTO operators (id, name, code_hash, active) VALUES (?, ?, ?, 1)'
  ).bind(id, name, await sha256Hex(code)).run();
  return { id, name, code, active: true, createdAt: new Date().toISOString() };
}
async function listOperators(env) {
  const result = await env.DB.prepare(
    `SELECT o.id, o.name, o.active, o.created_at,
       COUNT(d.id) AS device_count,
       SUM(CASE WHEN d.id IS NOT NULL AND d.revoked_at IS NULL THEN 1 ELSE 0 END) AS active_device_count
     FROM operators o LEFT JOIN devices d ON d.operator_id = o.id
     GROUP BY o.id ORDER BY o.name COLLATE NOCASE`
  ).all();
  return result.results || [];
}

export default {
  async fetch(request, env) {
    const origin = request.headers.get('Origin') || '';
    if (request.method === 'OPTIONS') {
      if (!ALLOWED_ORIGINS.has(origin)) return new Response(null, { status: 403 });
      return new Response(null, { status: 204, headers: corsHeaders(origin) });
    }
    if (origin && !ALLOWED_ORIGINS.has(origin)) return jsonResponse({ error: 'Origine non autorizzata.' }, 403, origin);
    const url = new URL(request.url);
    if (url.pathname === '/health' && request.method === 'GET')
      return jsonResponse({ ok: true, service: 'pallamano-referto-api', version: 2 }, 200, origin);

    if (!env.GITHUB_TOKEN || !env.APP_PASSWORD || !env.ADMIN_JWT_SECRET || !env.DB)
      return jsonResponse({ error: 'Configurazione server incompleta: verificare GITHUB_TOKEN, APP_PASSWORD, ADMIN_JWT_SECRET e il binding D1 DB.' }, 503, origin);

    try {
      if (url.pathname === '/api/login' && request.method === 'POST') {
        const key = await rateKey(request, 'admin-login');
        const state = await rateLimitState(env, key, 8, 900);
        if (state.blocked) return jsonResponse({ error: 'Troppi tentativi. Riprova tra qualche minuto.' }, 429, origin, { 'Retry-After': String(state.retryAfter) });
        const body = await readRequestJson(request);
        if (typeof body.password !== 'string' || !constantTimeEqual(body.password, env.APP_PASSWORD)) {
          const attempt = await recordFailedAttempt(env, key, 8, 900);
          if (attempt.blocked) return jsonResponse({ error: 'Troppi tentativi. Accesso temporaneamente bloccato.' }, 429, origin, { 'Retry-After': '900' });
          return jsonResponse({ error: 'Password non corretta.' }, 401, origin);
        }
        await clearRateLimit(env, key);
        return jsonResponse({ token: await issueAdminJwt(env.ADMIN_JWT_SECRET), expiresIn: ADMIN_TOKEN_TTL_SECONDS, role: 'admin' }, 200, origin);
      }

      if (url.pathname === '/api/operator/activate' && request.method === 'POST') {
        const key = await rateKey(request, 'operator-activate');
        const state = await rateLimitState(env, key, 10, 900);
        if (state.blocked) return jsonResponse({ error: 'Troppi tentativi di attivazione. Riprova tra qualche minuto.' }, 429, origin, { 'Retry-After': String(state.retryAfter) });
        const body = await readRequestJson(request);
        const code = typeof body.code === 'string' ? body.code.trim() : '';
        const deviceName = typeof body.deviceName === 'string' && body.deviceName.trim()
          ? body.deviceName.trim().slice(0, 100) : 'Dispositivo';
        if (code.length < 12 || code.length > 100) {
          const attempt = await recordFailedAttempt(env, key, 10, 900);
          return jsonResponse({ error: attempt.blocked ? 'Troppi tentativi di attivazione. Riprova tra qualche minuto.' : 'Codice personale non valido.' }, attempt.blocked ? 429 : 400, origin, attempt.blocked ? { 'Retry-After': '900' } : {});
        }
        const operator = await env.DB.prepare(
          'SELECT id, name FROM operators WHERE code_hash = ? AND active = 1 LIMIT 1'
        ).bind(await sha256Hex(code)).first();
        if (!operator) {
          const attempt = await recordFailedAttempt(env, key, 10, 900);
          if (attempt.blocked) return jsonResponse({ error: 'Troppi tentativi di attivazione. Accesso temporaneamente bloccato.' }, 429, origin, { 'Retry-After': '900' });
          return jsonResponse({ error: 'Codice non riconosciuto oppure operatore disattivato.' }, 401, origin);
        }
        await clearRateLimit(env, key);
        const token = randomSecret(32);
        const deviceId = makeId('dev');
        await env.DB.prepare(
          'INSERT INTO devices (id, operator_id, device_name, token_hash, last_seen_at) VALUES (?, ?, ?, ?, CURRENT_TIMESTAMP)'
        ).bind(deviceId, operator.id, deviceName, await sha256Hex(token)).run();
        return jsonResponse({ token, deviceId, operatorId: operator.id, operatorName: operator.name }, 200, origin);
      }

      const auth = await authenticate(request, env);
      if (!auth) return jsonResponse({ error: 'Accesso non autorizzato. Effettua nuovamente l’attivazione.' }, 401, origin);

      if (url.pathname === '/api/me' && request.method === 'GET') {
        return jsonResponse({
          role: auth.role,
          operatorId: auth.operatorId || null,
          operatorName: auth.operatorName || (auth.role === 'admin' ? 'Amministratore' : null),
          deviceId: auth.deviceId || null
        }, 200, origin);
      }

      if (url.pathname === '/api/admin/operators') {
        if (!requireAdmin(auth)) return jsonResponse({ error: 'Operazione riservata all’amministratore.' }, 403, origin);
        if (request.method === 'GET') return jsonResponse({ operators: await listOperators(env) }, 200, origin);
        if (request.method === 'POST') {
          const created = await createOperator(env, await readRequestJson(request));
          return jsonResponse(created, 201, origin);
        }
        return jsonResponse({ error: 'Metodo non consentito.' }, 405, origin, { Allow: 'GET, POST, OPTIONS' });
      }

      const devicesMatch = url.pathname.match(/^\/api\/admin\/operators\/([A-Za-z0-9_-]+)\/devices$/);
      if (devicesMatch && request.method === 'GET') {
        if (!requireAdmin(auth)) return jsonResponse({ error: 'Operazione riservata all’amministratore.' }, 403, origin);
        const result = await env.DB.prepare(
          `SELECT id, device_name, created_at, last_seen_at, revoked_at
           FROM devices WHERE operator_id = ?
           ORDER BY created_at DESC`
        ).bind(devicesMatch[1]).all();
        return jsonResponse({ devices: result.results || [] }, 200, origin);
      }

      const operatorMatch = url.pathname.match(/^\/api\/admin\/operators\/([A-Za-z0-9_-]+)$/);
      if (operatorMatch && request.method === 'PATCH') {
        if (!requireAdmin(auth)) return jsonResponse({ error: 'Operazione riservata all’amministratore.' }, 403, origin);
        const body = await readRequestJson(request);
        if (typeof body.active !== 'boolean') return jsonResponse({ error: 'Stato operatore non valido.' }, 400, origin);
        const result = await env.DB.prepare('UPDATE operators SET active = ?, updated_at = CURRENT_TIMESTAMP WHERE id = ?')
          .bind(body.active ? 1 : 0, operatorMatch[1]).run();
        if (!result.meta?.changes) return jsonResponse({ error: 'Operatore non trovato.' }, 404, origin);
        return jsonResponse({ ok: true }, 200, origin);
      }

      const revokeMatch = url.pathname.match(/^\/api\/admin\/devices\/([A-Za-z0-9_-]+)\/revoke$/);
      if (revokeMatch && request.method === 'POST') {
        if (!requireAdmin(auth)) return jsonResponse({ error: 'Operazione riservata all’amministratore.' }, 403, origin);
        const result = await env.DB.prepare('UPDATE devices SET revoked_at = CURRENT_TIMESTAMP WHERE id = ? AND revoked_at IS NULL')
          .bind(revokeMatch[1]).run();
        if (!result.meta?.changes) return jsonResponse({ error: 'Dispositivo non trovato o già revocato.' }, 404, origin);
        return jsonResponse({ ok: true }, 200, origin);
      }

      let key = null, path = null;
      if (url.pathname === '/api/archive') { key = 'archive'; path = FILES.archive; }
      else if (url.pathname === '/api/matches/index') { key = 'index'; path = FILES.index; }
      else if (url.pathname.startsWith('/api/matches/')) {
        let id;
        try { id = decodeURIComponent(url.pathname.slice('/api/matches/'.length)); }
        catch { return jsonResponse({ error: 'Identificativo partita non valido.' }, 400, origin); }
        path = matchPath(id); key = 'match';
        if (!path) return jsonResponse({ error: 'Identificativo partita non valido.' }, 400, origin);
      } else {
        return jsonResponse({ error: 'Percorso API non trovato.' }, 404, origin);
      }
      if (request.method === 'GET') {
        const result = await readFile(path, env.GITHUB_TOKEN);
        return jsonResponse({ ...result, key }, 200, origin);
      }
      if (request.method === 'PUT') {
        const body = await readRequestJson(request);
        if (!Object.prototype.hasOwnProperty.call(body, 'data')) return jsonResponse({ error: 'Campo data mancante.' }, 400, origin);
        const result = await writeFile(path, body.data, body.sha ?? null, env.GITHUB_TOKEN);
        if (result.conflict) return jsonResponse({ error: 'Conflitto: il file è stato modificato da un altro dispositivo. Ricarica i dati prima di salvare.', ...result }, 409, origin);
        return jsonResponse({ ok: true, ...result }, 200, origin);
      }
      return jsonResponse({ error: 'Metodo non consentito.' }, 405, origin, { Allow: 'GET, PUT, POST, PATCH, OPTIONS' });
    } catch (error) {
      return jsonResponse({ error: error.message || 'Errore interno.' }, 400, origin);
    }
  }
};
