// Bilem Agent — спайк: минимальный MCP-сервер поверх OData 1С.
// Только чтение, слушает только 127.0.0.1, адрес с токеном в пути: http://127.0.0.1:<порт>/mcp/<токен>.
// Базы — описания *.base.json, которые пишет spike/setup-spike.ps1 (пароль bilem — в DPAPI-файле рядом).
//
//   node server.mjs          — запустить сервер
//   node server.mjs --url    — вывести адрес с токеном (для claude mcp add) и выйти
//
// Переменные окружения: BILEM_MCP_PORT (по умолчанию 8795), BILEM_SPIKE_DIR (по умолчанию
// C:\ProgramData\Bilem\spike), BILEM_BASES — через запятую slug-и баз, если нужны не все.

import http from 'node:http';
import crypto from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import { execFileSync } from 'node:child_process';
import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { StreamableHTTPServerTransport } from '@modelcontextprotocol/sdk/server/streamableHttp.js';
import { z } from 'zod';

const PORT = Number(process.env.BILEM_MCP_PORT || 8795);
const SPIKE_DIR = process.env.BILEM_SPIKE_DIR || 'C:\\ProgramData\\Bilem\\spike';
const TOKEN_FILE = path.join(process.env.LOCALAPPDATA || SPIKE_DIR, 'Bilem', 'spike', 'mcp-token');

// ---------- Токен: создаётся при первом запуске, хранится локально в профиле пользователя ----------
function loadToken() {
  if (!fs.existsSync(TOKEN_FILE)) {
    fs.mkdirSync(path.dirname(TOKEN_FILE), { recursive: true });
    fs.writeFileSync(TOKEN_FILE, crypto.randomBytes(32).toString('base64url'), { encoding: 'ascii' });
  }
  return fs.readFileSync(TOKEN_FILE, 'ascii').trim();
}
const TOKEN = loadToken();
if (process.argv.includes('--url')) {
  console.log(`http://127.0.0.1:${PORT}/mcp/${TOKEN}`);
  process.exit(0);
}
function tokenOk(candidate) {
  const a = Buffer.from(String(candidate || ''));
  const b = Buffer.from(TOKEN);
  return a.length === b.length && crypto.timingSafeEqual(a, b);
}

// ---------- Базы ----------
// Пароль bilem расшифровывается из DPAPI-файла через Windows PowerShell (та же учётная запись Windows,
// что запускала setup-spike.ps1). PSModulePath — только WindowsPowerShell: иначе при запуске из
// PowerShell 7 модули 7-ки ломают ConvertTo-SecureString в 5.1.
function dpapiPassword(credFile) {
  const env = { ...process.env, BILEM_CRED: credFile };
  if (env.PSModulePath) env.PSModulePath = env.PSModulePath.split(';').filter((p) => /\\WindowsPowerShell\\/i.test(p)).join(';');
  const ps = '$s = Get-Content -LiteralPath $env:BILEM_CRED | ConvertTo-SecureString; ' +
    "[Console]::Out.Write((New-Object System.Management.Automation.PSCredential('bilem', $s)).GetNetworkCredential().Password)";
  return execFileSync('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command', ps], { env, encoding: 'utf8', windowsHide: true }).trim();
}

function loadBases() {
  const only = (process.env.BILEM_BASES || '').split(',').map((s) => s.trim()).filter(Boolean);
  const files = fs.existsSync(SPIKE_DIR) ? fs.readdirSync(SPIKE_DIR).filter((f) => f.endsWith('.base.json')) : [];
  const bases = [];
  for (const f of files) {
    const d = JSON.parse(fs.readFileSync(path.join(SPIKE_DIR, f), 'utf8').replace(/^\uFEFF/, ''));
    if (only.length && !only.includes(d.slug)) continue;
    const pwd = dpapiPassword(d.credFile);
    bases.push({ ...d, auth: 'Basic ' + Buffer.from(`${d.user}:${pwd}`, 'utf8').toString('base64'), cache: {} });
  }
  return bases.sort((a, b) => a.slug.localeCompare(b.slug));
}
const BASES = loadBases();

function pickBase(slug) {
  if (slug) {
    const b = BASES.find((x) => x.slug === slug);
    if (!b) throw new Error(`Нет базы «${slug}». Подключены: ${BASES.map((x) => x.slug).join(', ') || 'ни одной'}.`);
    return b;
  }
  if (BASES.length === 1) return BASES[0];
  if (!BASES.length) throw new Error('Не подключено ни одной базы: запустите setup-spike.ps1 -Apply.');
  throw new Error(`Подключено несколько баз — укажите параметр base: ${BASES.map((x) => x.slug).join(', ')}.`);
}

// ---------- OData ----------
async function odata(b, resource, query = {}) {
  const qs = Object.entries({ ...query, $format: 'json' }).map(([k, v]) => `${k}=${encodeURIComponent(v)}`).join('&');
  const r = await fetch(`${b.odata}/${resource}?${qs}`, {
    headers: { Authorization: b.auth, Accept: 'application/json' },
    signal: AbortSignal.timeout(60000),
  });
  const text = await r.text();
  if (!r.ok) {
    let msg = text.slice(0, 200);
    try { msg = JSON.parse(text)['odata.error'].message.value; } catch { /* не JSON */ }
    throw new Error(`1С ответила HTTP ${r.status}: ${msg}`);
  }
  return JSON.parse(text).value;
}
const q = (s) => String(s).replace(/'/g, "''"); // строка в литерале OData
const money = (n) => Math.round((Number(n) || 0) * 100) / 100;

// Справочники, которые меняются редко (организации, план счетов), — кэш на 10 минут
const CACHE_MS = 10 * 60 * 1000;
async function cached(b, key, load) {
  const c = b.cache[key];
  if (c && Date.now() - c.at < CACHE_MS) return c.value;
  const value = await load();
  b.cache[key] = { at: Date.now(), value };
  return value;
}

function organizations(b) {
  return cached(b, 'orgs', () => odata(b, 'Catalog_Организации', {
    $select: 'Ref_Key,Description,ИдентификационныйНомер',
    $filter: 'DeletionMark eq false',
  }));
}

// Счета денежных средств плана счетов «Типовой» (БП КЗ): группа 10xx.
// Касса — 1010; банк — 1030, 1040, 1050; прочие — остальные 10xx (в пути, ограниченные, электронные, резерв…).
function cashClass(code) {
  if (code.startsWith('1010')) return 'cash';
  if (/^10[345]0/.test(code)) return 'bank';
  return 'other';
}
function moneyAccounts(b) {
  return cached(b, 'accounts', async () => {
    const all = await odata(b, 'ChartOfAccounts_Типовой', { $select: 'Ref_Key,Code' });
    return all.filter((a) => /^10\d\d/.test(a.Code) && a.Code !== '1000').map((a) => ({ key: a.Ref_Key, code: a.Code, cls: cashClass(a.Code) }));
  });
}

// ---------- MCP ----------
const baseParam = z.string().optional().describe('Короткое имя базы из list_bases. Можно не указывать, если база одна.');
const json = (obj) => ({ content: [{ type: 'text', text: JSON.stringify(obj) }] });

function buildServer() {
  const mcp = new McpServer({ name: 'bilem-agent-spike', version: '0.0.1' });

  // Обёртка: время ответа, понятная ошибка, журнал без данных (инструмент, база, мс, итог)
  const tool = (name, meta, fn) => mcp.registerTool(name, meta, async (args) => {
    const t0 = Date.now();
    try {
      const out = await fn(args || {});
      const ms = Date.now() - t0;
      console.log(`${new Date().toISOString()} ${name} base=${out.base ?? '-'} ok ${ms} ms`);
      return json({ ...out, ms });
    } catch (e) {
      console.log(`${new Date().toISOString()} ${name} error ${Date.now() - t0} ms`);
      return { isError: true, content: [{ type: 'text', text: e.message }] };
    }
  });

  tool('list_bases', {
    title: 'Подключённые базы 1С',
    description: 'Список баз 1С, подключённых к Bilem Agent на этом компьютере: короткое имя (base), конфигурация, версия. Вызывай первым, если не знаешь, какие базы есть, или если пользователь не уточнил базу.',
    inputSchema: {},
  }, async () => ({
    bases: BASES.map((b) => ({ base: b.slug, configuration: b.synonym || b.configuration, version: b.version })),
  }));

  tool('whoami', {
    title: 'Что за база и организация',
    description: 'Сведения о базе 1С: конфигурация и версия, организации (наименование и БИН/ИИН), пользователь, под которым читаем. Вызывай на вопросы «что это за организация», «чья это база», «какие у нас организации».',
    inputSchema: { base: baseParam },
  }, async ({ base }) => {
    const b = pickBase(base);
    const orgs = await organizations(b);
    return {
      base: b.slug,
      configuration: b.synonym || b.configuration,
      version: b.version,
      user: b.user,
      organizations: orgs.map((o) => ({ name: o.Description, bin: o.ИдентификационныйНомер || null })),
    };
  });

  tool('find_counterparty', {
    title: 'Найти контрагента',
    description: 'Поиск контрагента (покупателя, поставщика) в базе 1С по части наименования или по БИН/ИИН (цифры). Возвращает до 10 найденных: наименование, полное наименование, БИН/ИИН. Вызывай, когда пользователь называет компанию или БИН и нужно понять, о ком речь.',
    inputSchema: {
      query: z.string().min(2).describe('Часть наименования или БИН/ИИН (можно часть цифр).'),
      base: baseParam,
    },
  }, async ({ query, base }) => {
    const b = pickBase(base);
    const s = query.trim();
    const byBin = /^\d+$/.test(s);
    const cond = byBin
      ? (s.length === 12 ? `ИдентификационныйКодЛичности eq '${q(s)}'` : `substringof('${q(s)}', ИдентификационныйКодЛичности)`)
      : `substringof('${q(s)}', Description)`;
    const rows = await odata(b, 'Catalog_Контрагенты', {
      $select: 'Description,НаименованиеПолное,ИдентификационныйКодЛичности',
      $filter: `${cond} and IsFolder eq false and DeletionMark eq false`,
      $orderby: 'Description',
      $top: '11',
    });
    return {
      base: b.slug,
      found: Math.min(rows.length, 10),
      more: rows.length > 10,
      items: rows.slice(0, 10).map((r) => ({ name: r.Description, fullName: r.НаименованиеПолное || undefined, bin: r.ИдентификационныйКодЛичности || null })),
    };
  });

  tool('cash_balance', {
    title: 'Остаток денег',
    description: 'Остаток денежных средств на конец дня по каждой организации базы 1С, по бухгалтерскому учёту (план счетов «Типовой»): касса (1010), банк (1030, 1040, 1050), прочие (остальные счета 10xx: в пути, ограниченные, электронные и др.) и итого, в валюте регламентированного учёта (тенге). Вызывай на вопросы «сколько у нас денег», «что на счетах», «остаток в кассе».',
    inputSchema: {
      date: z.string().regex(/^\d{4}-\d{2}-\d{2}$/).optional().describe('Дата ГГГГ-ММ-ДД, остаток на конец этого дня. По умолчанию — сегодня.'),
      base: baseParam,
    },
  }, async ({ date, base }) => {
    const b = pickBase(base);
    const d = date || new Date(Date.now() - new Date().getTimezoneOffset() * 60000).toISOString().slice(0, 10);
    const accounts = await moneyAccounts(b);
    const byKey = new Map(accounts.map((a) => [a.key, a]));
    // Условие по счетам — в $filter (строка запроса), а не в AccountCondition: длинное условие в сегменте
    // пути упирается в лимит http.sys на длину сегмента (260 символов) и получает 400 от IIS.
    const rows = await odata(b, `AccountingRegister_Типовой/Balance(Period=datetime'${d}T23:59:59')`, {
      $filter: accounts.map((a) => `Account_Key eq guid'${a.key}'`).join(' or '),
    });
    const orgs = new Map((await organizations(b)).map((o) => [o.Ref_Key, o.Description]));
    const per = new Map();
    for (const r of rows) {
      const a = byKey.get(r.Account_Key);
      if (!a) continue;
      const o = per.get(r.Организация_Key) || { organization: orgs.get(r.Организация_Key) || r.Организация_Key, cash: 0, bank: 0, other: 0 };
      o[a.cls] += Number(r.СуммаBalance) || 0;
      per.set(r.Организация_Key, o);
    }
    const byOrg = [...per.values()].map((o) => ({
      organization: o.organization, cash: money(o.cash), bank: money(o.bank), other: money(o.other), total: money(o.cash + o.bank + o.other),
    }));
    return { base: b.slug, date: d, currency: 'KZT', organizations: byOrg };
  });

  return mcp;
}

// ---------- HTTP: только 127.0.0.1, токен в пути, без сессий ----------
const httpServer = http.createServer(async (req, res) => {
  const url = new URL(req.url, 'http://127.0.0.1');
  const m = url.pathname.match(/^\/mcp(?:\/([^/]*))?\/?$/);
  const deny = (code, error) => { res.writeHead(code, { 'Content-Type': 'application/json' }).end(JSON.stringify({ error })); };
  if (!m) return deny(404, 'not_found');
  if (!tokenOk(m[1])) { console.log(`${new Date().toISOString()} 401 ${req.method}`); return deny(401, 'unauthorized'); }
  // Запросы со страниц браузера (есть Origin) не принимаем: MCP-клиенты Origin не шлют
  if (req.headers.origin) return deny(403, 'origin_not_allowed');
  if (req.method !== 'POST') { res.setHeader('Allow', 'POST'); return deny(405, 'method_not_allowed'); }
  let body;
  try {
    const chunks = [];
    for await (const c of req) chunks.push(c);
    body = JSON.parse(Buffer.concat(chunks).toString('utf8'));
  } catch { return deny(400, 'bad_json'); }
  const mcp = buildServer();
  const transport = new StreamableHTTPServerTransport({ sessionIdGenerator: undefined, enableJsonResponse: true });
  res.on('close', () => { transport.close(); mcp.close(); });
  try {
    await mcp.connect(transport);
    await transport.handleRequest(req, res, body);
  } catch (e) {
    console.log(`${new Date().toISOString()} 500 ${e.message}`);
    if (!res.headersSent) deny(500, 'internal_error');
  }
});
httpServer.listen(PORT, '127.0.0.1', () => {
  console.log(`Bilem MCP (спайк): http://127.0.0.1:${PORT}/mcp/<токен>  — адрес с токеном: node server.mjs --url`);
  console.log(`Баз подключено: ${BASES.length}${BASES.length ? ' (' + BASES.map((b) => b.slug).join(', ') + ')' : ''}`);
});
