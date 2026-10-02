// Compress backend: anonymous usage counters + feedback relay to Discord.
// The app never talks to Discord directly, so the webhook URLs stay secret on this server.

const EVENTS = new Set(["install", "active", "export"]);
const TOOLS = new Set(["compress", "cutter", "resize", "shorts", "converter"]);
const VERSION = /^\d{1,3}\.\d{1,3}\.\d{1,3}$/;
const MAX_FEEDBACK_BYTES = 1_000_000;

const today = () => new Date().toISOString().slice(0, 10);
const daysAgo = (n) => new Date(Date.now() - n * 86400000).toISOString().slice(0, 10);

export default {
  async fetch(request, env) {
    const url = new URL(request.url);
    if (request.method === "POST" && url.pathname === "/ping") return ping(request, env);
    if (request.method === "POST" && url.pathname === "/feedback") return feedback(request, env);
    return new Response("Compress", { status: 404 });
  },

  async scheduled(_event, env, ctx) {
    ctx.waitUntil(postSummary(env));
  },
};

async function ping(request, env) {
  let body;
  try {
    body = await request.json();
  } catch {
    return new Response("bad json", { status: 400 });
  }
  const event = String(body.e ?? "");
  const detail = event === "export" ? String(body.t ?? "") : String(body.v ?? "");
  if (!EVENTS.has(event)) return new Response("bad event", { status: 400 });
  if (event === "export" ? !TOOLS.has(detail) : !VERSION.test(detail)) return new Response("bad detail", { status: 400 });

  await env.DB.prepare(
    "INSERT INTO counts (day, event, detail, n) VALUES (?1, ?2, ?3, 1) " +
      "ON CONFLICT (day, event, detail) DO UPDATE SET n = n + 1"
  ).bind(today(), event, detail).run();
  return new Response(null, { status: 204 });
}

async function feedback(request, env) {
  const type = request.headers.get("content-type") ?? "";
  if (!type.startsWith("multipart/form-data")) return new Response("bad type", { status: 400 });
  const size = Number(request.headers.get("content-length") ?? 0);
  if (size > MAX_FEEDBACK_BYTES) return new Response("too large", { status: 413 });

  const res = await fetch(env.FEEDBACK_WEBHOOK, {
    method: "POST",
    headers: { "content-type": type },
    body: request.body,
  });
  return new Response(null, { status: res.ok ? 204 : res.status === 429 ? 429 : 502 });
}

async function sum(env, sql, ...args) {
  const row = await env.DB.prepare(sql).bind(...args).first();
  return row?.n ?? 0;
}

async function postSummary(env) {
  const day = daysAgo(1);
  const weekStart = daysAgo(7);

  const active = await sum(env, "SELECT SUM(n) AS n FROM counts WHERE day = ?1 AND event = 'active'", day);
  const installs = await sum(env, "SELECT SUM(n) AS n FROM counts WHERE day = ?1 AND event = 'install'", day);
  const installsTotal = await sum(env, "SELECT SUM(n) AS n FROM counts WHERE event = 'install'");
  const weekActive = await sum(env, "SELECT SUM(n) AS n FROM counts WHERE day >= ?1 AND day <= ?2 AND event = 'active'", weekStart, day);
  const exportsTotal = await sum(env, "SELECT SUM(n) AS n FROM counts WHERE event = 'export'");

  const tools = (await env.DB.prepare(
    "SELECT detail, SUM(n) AS n FROM counts WHERE day = ?1 AND event = 'export' GROUP BY detail ORDER BY n DESC"
  ).bind(day).all()).results;
  const versions = (await env.DB.prepare(
    "SELECT detail, SUM(n) AS n FROM counts WHERE day = ?1 AND event = 'active' GROUP BY detail ORDER BY detail DESC"
  ).bind(day).all()).results;

  const exportsDay = tools.reduce((a, r) => a + r.n, 0);
  const name = { compress: "Compress", cutter: "Cutter", resize: "Resize", shorts: "Shorts", converter: "Converter" };
  const list = (rows, label) => rows.length ? rows.map((r) => `${label(r.detail)}: **${r.n}**`).join("\n") : "–";

  const embed = {
    title: `Compress stats · ${day}`,
    color: 0x84cc16,
    fields: [
      { name: "Active users", value: `**${active}**`, inline: true },
      { name: "New installs", value: `**${installs}**`, inline: true },
      { name: "Videos exported", value: `**${exportsDay}**`, inline: true },
      { name: "Exports by tool", value: list(tools, (t) => name[t] ?? t), inline: true },
      { name: "Versions in use", value: list(versions, (v) => `v${v}`), inline: true },
      { name: "\u200b", value: "\u200b", inline: true },
      { name: "Installs all time", value: `**${installsTotal}**`, inline: true },
      { name: "Avg. active / day (7 days)", value: `**${(weekActive / 7).toFixed(1)}**`, inline: true },
      { name: "Videos all time", value: `**${exportsTotal}**`, inline: true },
    ],
    footer: { text: "Anonymous counts · days in UTC" },
  };

  await fetch(env.STATS_WEBHOOK, {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify({ username: "Compress Stats", embeds: [embed] }),
  });
}
