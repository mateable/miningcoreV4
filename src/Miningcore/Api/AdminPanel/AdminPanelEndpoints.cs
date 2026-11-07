using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Miningcore.Admin;
using Miningcore.Configuration;

namespace Miningcore.Api.AdminPanel;

public static class AdminPanelEndpoints
{
    private const string CookieName = "mcce_session";
    private static readonly ConcurrentDictionary<string, (int failures, DateTime? bannedUntil)> loginTracker = new();

    /// <summary>
    /// Register admin routes on a standalone web application (separate port).
    /// </summary>
    public static void MapAdminPanel(this WebApplication app, ClusterConfig clusterConfig, int apiPort)
    {
        var cfg = clusterConfig.AdminPanel;
        if(cfg?.Enabled != true)
            return;

        var apiBase = $"http://localhost:{apiPort}";

        var ipWhitelist = cfg.IpWhitelist?.Select(System.Net.IPAddress.Parse).ToArray();

        // Auth middleware — only protects /admin routes, not /api
        app.Use(async (ctx, next) =>
        {
            if(ctx.Request.Path.StartsWithSegments("/admin"))
            {
                if(ipWhitelist is { Length: > 0 })
                {
                    var remote = ctx.Connection.RemoteIpAddress;
                    if(remote == null || !ipWhitelist.Any(x => x.Equals(remote)))
                    {
                        ctx.Response.StatusCode = 403;
                        await ctx.Response.WriteAsync("You are not in my access list. Good Bye.\n");
                        return;
                    }
                }

                if(!ctx.Request.Path.StartsWithSegments("/admin/login") &&
                   !IsAuthenticated(ctx, cfg))
                {
                    ctx.Response.Redirect("/admin/login");
                    return;
                }
            }
            await next();
        });

        var admin = app.MapGroup("/admin");

        // Proxy API endpoints from the main host so the dashboard can fetch data
        app.MapGet("/api/pools", async ctx => await ProxyToApi(ctx, apiBase));
        app.MapGet("/api/pools/{**rest}", async (string rest, HttpContext ctx) => await ProxyToApi(ctx, apiBase));
        app.MapGet("/api/blocks", async ctx => await ProxyToApi(ctx, apiBase));
        app.MapGet("/api/admin/{**rest}", async (string rest, HttpContext ctx) => await ProxyToApi(ctx, apiBase));
        app.MapPost("/api/admin/{**rest}", async (string rest, HttpContext ctx) => await ProxyToApi(ctx, apiBase));

        // === Login page (HTML) ===
        admin.MapGet("/login", () =>
        {
            return Results.Content(LoginPageHtml, "text/html");
        });

        // === Login action ===
        admin.MapPost("/login", async (HttpContext ctx) =>
        {
            var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";

            if(loginTracker.TryGetValue(ip, out var entry) && entry.bannedUntil > DateTime.UtcNow)
            {
                ctx.Response.StatusCode = 429;
                await ctx.Response.WriteAsync("Too many attempts. Try again later.");
                return;
            }

            var form = await ctx.Request.ReadFormAsync();
            var password = form["password"].ToString();

            if(!string.IsNullOrEmpty(cfg.Password) && password == cfg.Password)
            {
                loginTracker.TryRemove(ip, out _);

                var sessionData = $"{ip}|{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
                var signature = Sign(sessionData, cfg.Password);
                var cookieValue = $"{sessionData}|{signature}";

                ctx.Response.Cookies.Append(CookieName, cookieValue, new CookieOptions
                {
                    HttpOnly = true,
                    SameSite = SameSiteMode.Strict,
                    MaxAge = TimeSpan.FromSeconds(cfg.SessionTimeout)
                });

                ctx.Response.Redirect("/admin");
            }
            else
            {
                var failures = (entry.failures + 1);
                var banned = failures >= cfg.MaxLoginAttempts
                    ? DateTime.UtcNow.AddSeconds(cfg.LoginBanDuration)
                    : (DateTime?) null;

                loginTracker.AddOrUpdate(ip,
                    _ => (failures, banned),
                    (_, _) => (failures, banned));

                ctx.Response.StatusCode = 401;
                await ctx.Response.WriteAsync("Wrong password.");
            }
        });

        // === Logout ===
        admin.MapGet("/logout", (HttpContext ctx) =>
        {
            ctx.Response.Cookies.Delete(CookieName);
            ctx.Response.Redirect("/admin/login");
        });

        // === Change password ===
        // In-memory only: does not rewrite config.json, so it reverts on
        // restart. Documented, not silently incomplete - see docs/ADMIN_PANEL.md.
        admin.MapPost("/change-password", async (HttpContext ctx) =>
        {
            if(!IsAuthenticated(ctx, cfg))
            {
                ctx.Response.StatusCode = 401;
                return;
            }

            var form = await ctx.Request.ReadFormAsync();
            var current = form["current"].ToString();
            var next = form["next"].ToString();

            if(!string.IsNullOrEmpty(cfg.Password) && current != cfg.Password)
            {
                ctx.Response.StatusCode = 401;
                await ctx.Response.WriteAsync("Current password is wrong.");
                return;
            }

            if(string.IsNullOrWhiteSpace(next))
            {
                ctx.Response.StatusCode = 400;
                await ctx.Response.WriteAsync("New password cannot be empty.");
                return;
            }

            cfg.Password = next;
            AdminAuditLog.Record(ctx.Connection.RemoteIpAddress?.ToString(), "admin.change-password",
                "Admin panel password changed (in-memory only, reverts on restart)");

            // All existing sessions are signed with the old password - invalidate them.
            ctx.Response.Cookies.Delete(CookieName);
            await ctx.Response.WriteAsync("Ok");
        });

        // === Dashboard (HTML) ===
        admin.MapGet("/", (HttpContext ctx) =>
        {
            return Results.Content(DashboardHtml, "text/html");
        });
    }

    private static bool IsAuthenticated(HttpContext ctx, AdminPanelConfig cfg)
    {
        if(string.IsNullOrEmpty(cfg.Password))
            return true;

        if(!ctx.Request.Cookies.TryGetValue(CookieName, out var cookie))
            return false;

        var parts = cookie.Split('|');
        if(parts.Length != 3)
            return false;

        var ip = parts[0];
        var tsStr = parts[1];
        var providedSig = parts[2];

        if(ip != (ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown"))
            return false;

        if(long.TryParse(tsStr, out var ts))
        {
            var sessionTime = DateTimeOffset.FromUnixTimeSeconds(ts);
            if((DateTimeOffset.UtcNow - sessionTime).TotalSeconds > cfg.SessionTimeout)
                return false;
        }
        else return false;

        var expectedSig = Sign($"{ip}|{tsStr}", cfg.Password);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(providedSig),
            Encoding.UTF8.GetBytes(expectedSig));
    }

    private static string Sign(string data, string key)
    {
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(data));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static readonly HttpClient apiClient = new();

    private static async Task ProxyToApi(HttpContext ctx, string apiBase)
    {
        var url = $"{apiBase}{ctx.Request.Path}{ctx.Request.QueryString}";
        var req = new HttpRequestMessage(new HttpMethod(ctx.Request.Method), url);

        if(ctx.Request.Body.CanRead && ctx.Request.Method != "GET")
        {
            req.Content = new StreamContent(ctx.Request.Body);
            req.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(ctx.Request.ContentType ?? "application/json");
        }

        using var resp = await apiClient.SendAsync(req);
        ctx.Response.StatusCode = (int) resp.StatusCode;
        ctx.Response.ContentType = resp.Content.Headers.ContentType?.ToString() ?? "application/json";
        await resp.Content.CopyToAsync(ctx.Response.Body);
    }

    private const string LoginPageHtml = @"<!DOCTYPE html>
<html lang=""en""><head><meta charset=""UTF-8""><meta name=""viewport"" content=""width=device-width, initial-scale=1""><title>MCCE Admin</title>
<style>*{margin:0;padding:0;box-sizing:border-box}body{background:#0d1117;color:#e6edf3;font:16px system-ui,sans-serif;display:flex;align-items:center;justify-content:center;min-height:100vh}.box{background:#161b22;border:1px solid #30363d;border-radius:12px;padding:36px;width:380px;box-shadow:0 8px 24px rgba(0,0,0,.3)}h1{font-size:22px;margin-bottom:6px;color:#e6edf3}.desc{color:#8b949e;font-size:13px;margin-bottom:24px}input{width:100%;padding:12px 14px;background:#0d1117;border:1px solid #30363d;border-radius:8px;color:#e6edf3;font-size:15px;margin-bottom:16px}input:focus{outline:none;border-color:#58a6ff;box-shadow:0 0 0 3px rgba(88,166,255,.15)}button{width:100%;padding:12px;background:#238636;color:#fff;border:0;border-radius:8px;font-size:15px;font-weight:600;cursor:pointer}button:hover{background:#2ea043}</style></head>
<body><div class=""box""><h1>⚡ MCCE Admin</h1><p class=""desc"">Enter your admin password to continue</p><form method=""post""><input type=""password"" name=""password"" placeholder=""Password"" autofocus><button type=""submit"">Sign in</button></form></div></body></html>";

    private const string DashboardHtml = @"<!DOCTYPE html>
<html lang=""en""><head><meta charset=""UTF-8""><meta name=""viewport"" content=""width=device-width,initial-scale=1""><title>MCCE Admin</title>
<style>
:root{--bg:#0d1117;--card:#161b22;--border:#30363d;--text:#e6edf3;--muted:#8b949e;--accent:#f78166;--green:#3fb950;--red:#f85149;--yellow:#d29922}
*{margin:0;padding:0;box-sizing:border-box}
body{background:var(--bg);color:var(--text);font:13px/1.5 system-ui,-apple-system,sans-serif;display:flex;min-height:100vh}
#sidebar{width:220px;background:var(--card);border-right:1px solid var(--border);padding:20px 0;position:fixed;top:0;left:0;bottom:0;overflow-y:auto}
#sidebar .logo{font-size:18px;font-weight:700;color:var(--text);padding:0 20px 20px;border-bottom:1px solid var(--border);margin-bottom:12px}
#sidebar a{display:flex;align-items:center;gap:10px;padding:10px 20px;color:var(--muted);text-decoration:none;font-size:13px;transition:all .15s}
#sidebar a:hover,#sidebar a.active{color:var(--text);background:rgba(255,255,255,.04)}
#sidebar a.active{border-right:2px solid var(--accent)}
#sidebar a .icon{font-size:16px;width:20px;text-align:center}
#main{margin-left:220px;flex:1;padding:0}
#topbar{background:var(--card);border-bottom:1px solid var(--border);padding:12px 24px;display:flex;justify-content:space-between;align-items:center;position:sticky;top:0;z-index:10}
#topbar h1{font-size:15px;font-weight:600;color:var(--text)}
#topbar .right{display:flex;align-items:center;gap:16px;font-size:12px;color:var(--muted)}
#topbar a{color:var(--muted);text-decoration:none}#topbar a:hover{color:var(--text)}
#content{padding:24px;max-width:1400px}
.stats{display:grid;grid-template-columns:repeat(auto-fit,minmax(220px,1fr));gap:14px;margin-bottom:24px}
.stat{background:var(--card);border:1px solid var(--border);border-radius:10px;padding:18px 20px}
.stat .top{display:flex;align-items:center;gap:10px;margin-bottom:10px}
.stat .icon{font-size:20px}
.stat .label{font-size:11px;color:var(--muted);text-transform:uppercase;letter-spacing:.5px}
.stat .value{font-size:26px;font-weight:700;color:var(--text);margin-top:2px}
.stat .sub{font-size:11px;color:var(--muted);margin-top:4px}
.panel{background:var(--card);border:1px solid var(--border);border-radius:10px;margin-bottom:16px}
.panel-header{padding:14px 20px;border-bottom:1px solid var(--border);font-size:14px;font-weight:600;display:flex;justify-content:space-between;align-items:center}
.panel-body{padding:20px}
table{width:100%;border-collapse:collapse}
th{text-align:left;font-size:11px;text-transform:uppercase;letter-spacing:.5px;color:var(--muted);padding:10px 16px;border-bottom:1px solid var(--border)}
td{padding:10px 16px;border-bottom:1px solid rgba(48,54,61,.5);font-size:13px}
tr:hover td{background:rgba(255,255,255,.02)}
.green{color:var(--green)}.red{color:var(--red)}.yellow{color:var(--yellow)}
.badge{display:inline-block;padding:2px 8px;border-radius:12px;font-size:11px;font-weight:500}
.badge-ok{background:rgba(63,185,80,.15);color:var(--green)}
.badge-warn{background:rgba(210,153,34,.15);color:var(--yellow)}
input,select{background:var(--bg);border:1px solid var(--border);border-radius:6px;color:var(--text);padding:8px 12px;font-size:13px;width:100%}
input:focus{outline:none;border-color:#58a6ff}
.btn{padding:8px 16px;border-radius:6px;font-size:13px;cursor:pointer;border:0;font-weight:500}
.btn-primary{background:#238636;color:#fff}.btn-primary:hover{background:#2ea043}
.btn-secondary{background:var(--bg);color:var(--text);border:1px solid var(--border)}.btn-secondary:hover{background:#21262d}
.btn-danger{background:rgba(248,81,73,.15);color:var(--red);border:1px solid rgba(248,81,73,.3)}.btn-danger:hover{background:rgba(248,81,73,.25)}
.row{display:flex;gap:16px;margin-bottom:16px}.col{flex:1}
.grid2{display:grid;grid-template-columns:1fr 1fr;gap:16px}
.loading{color:var(--muted);text-align:center;padding:40px}
@media(max-width:768px){#sidebar{display:none}#main{margin-left:0}}
</style></head>
<body>
<div id=""sidebar"">
 <div class=""logo"">⚡ MCCE Admin</div>
 <a href=""#"" class=""active"" onclick=""t('overview')""><span class=""icon"">📊</span> Overview</a>
 <a href=""#"" onclick=""t('pools')""><span class=""icon"">⛏️</span> Pools</a>
 <a href=""#"" onclick=""t('miners')""><span class=""icon"">👤</span> Miners</a>
 <a href=""#"" onclick=""t('blocks')""><span class=""icon"">🧱</span> Blocks</a>
 <a href=""#"" onclick=""t('payments')""><span class=""icon"">💰</span> Payments</a>
 <a href=""#"" onclick=""t('health')""><span class=""icon"">🩺</span> Health</a>
 <a href=""#"" onclick=""t('bans')""><span class=""icon"">🚫</span> Bans</a>
 <a href=""#"" onclick=""t('farms')""><span class=""icon"">🐝</span> Farms</a>
 <a href=""#"" onclick=""t('alerts')""><span class=""icon"">🔔</span> Alerts <span id=""alertBadge"" style=""display:none;background:var(--red);color:#fff;border-radius:10px;padding:1px 7px;font-size:11px;margin-left:auto""></span></a>
 <a href=""#"" onclick=""t('logs')""><span class=""icon"">📜</span> Logs</a>
 <a href=""#"" onclick=""t('auditlog')""><span class=""icon"">🕵️</span> Audit Log</a>
 <a href=""#"" onclick=""t('console')""><span class=""icon"">💻</span> Console</a>
 <a href=""#"" onclick=""t('coins')""><span class=""icon"">➕</span> Add Coin</a>
 <a href=""#"" onclick=""t('settings')""><span class=""icon"">⚙️</span> Settings</a>
</div>
<div id=""main"">
 <div id=""topbar""><h1 id=""page-title"">Overview</h1><div class=""right""><span id=""clock""></span><a href=""/admin/logout"">Logout</a></div></div>
 <div id=""content""><div class=""loading"">Loading...</div></div>
</div>
<script>
setInterval(function(){document.getElementById('clock').textContent=new Date().toLocaleString()},1000);
document.getElementById('clock').textContent=new Date().toLocaleString();

async function refreshAlertBadge(){
 try{
  var r=await fetch('/api/admin/alerts/active');
  if(!r.ok)return;
  var active=JSON.parse(await r.text());
  var badge=document.getElementById('alertBadge');
  if(active&&active.length){badge.textContent=active.length;badge.style.display='inline-block'}
  else badge.style.display='none';
 }catch(e){}
}
refreshAlertBadge();
setInterval(refreshAlertBadge,30000);

function payoutBadge(scheme){
 if(!scheme)return'';
 var s=scheme.toUpperCase();
 return '<span class=""badge '+(s=='SOLO'?'badge-warn':'badge-ok')+'"" style=""margin-left:8px"">'+s+'</span>';
}
function groupByCoin(pools){
 var groups={};
 for(var p of pools){var coinName=(p.coin&&p.coin.name)||p.id;(groups[coinName]=groups[coinName]||[]).push(p)}
 return groups;
}

function t(n){
 document.querySelectorAll('#sidebar a').forEach(a=>a.classList.remove('active'));
 event.target.closest('a').classList.add('active');
 document.getElementById('page-title').textContent=event.target.closest('a').textContent.trim();
 document.getElementById('content').innerHTML='<div class=""loading"">Loading...</div>';
 loaders[n]();
}
async function a(u){var r=await fetch(u);return r.json()}
async function pf(u,body){var r=await fetch(u,{method:'POST',headers:{'Content-Type':'application/x-www-form-urlencoded'},body:new URLSearchParams(body||{})});if(!r.ok)throw new Error(await r.text());return r}
async function pj(u,body){return fetch(u,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(body||{})})}
async function aj(u){var r=await fetch(u);if(!r.ok)throw new Error('http '+r.status);var t=await r.text();try{return JSON.parse(t)}catch(e){return t}}
function f(n,d=2){return n!=null?Number(n).toFixed(d):'0'}
// Difficulty values for low-difficulty algorithms (e.g. balloon, ~5e-7) round to
// all zeroes under f()'s fixed decimal places - looks like zero/broken even though
// the real value and share validation are fine. Switch to exponential notation for
// anything too small to show meaningfully at 4 decimal places.
function fdiff(n){if(n==null)return'0';n=Number(n);if(n===0)return'0';if(Math.abs(n)>=0.001)return n.toFixed(4);return n.toFixed(10).replace(/0+$/,'').replace(/\.$/,'')}
function h(v){if(!v)return'0 H/s';if(v>1e15)return f(v/1e15,2)+' PH/s';if(v>1e12)return f(v/1e12,2)+' TH/s';if(v>1e9)return f(v/1e9,2)+' GH/s';if(v>1e6)return f(v/1e6,2)+' MH/s';return f(v/1e3,2)+' KH/s'}
function ago(d){if(!d)return'—';var s=(Date.now()-new Date(d).getTime())/1000;if(s<60)return Math.round(s)+'s ago';if(s<3600)return Math.round(s/60)+'m ago';if(s<86400)return Math.round(s/3600)+'h ago';return Math.round(s/86400)+'d ago'}
function E(s){var t='';if(s=='Confirmed')t='badge-ok';else if(s=='Pending')t='badge-warn';return '<span class=""badge '+t+'"">'+s+'</span>';}

var loaders={
overview:async function(){
 var d=await a('/api/pools');
 if(!d.pools||!d.pools.length){document.getElementById('content').innerHTML='<div class=""loading"">No pools configured</div>';return}
 var groups=groupByCoin(d.pools),html='';
 for(var coinName in groups){
  html+='<div style=""margin:18px 0 8px;font-size:13px;font-weight:600;color:var(--muted);text-transform:uppercase;letter-spacing:.5px"">'+coinName+'</div>';
  html+='<div class=""stats"">';
  for(var p of groups[coinName]){var s=p.poolStats||{},n=p.networkStats||{},scheme=p.paymentProcessing&&p.paymentProcessing.payoutScheme;
   html+='<div class=""stat""><div class=""top""><span class=""icon"">⛏️</span><span class=""label"">'+p.id+payoutBadge(scheme)+' Hashrate</span></div><div class=""value"">'+h(s.poolHashrate||0)+'</div><div class=""sub"">'+s.connectedMiners+' miners · '+f(s.sharesPerSecond||0)+' shares/s</div></div>';
   html+='<div class=""stat""><div class=""top""><span class=""icon"">🌐</span><span class=""label"">Network</span></div><div class=""value"">'+h(n.networkHashrate||0)+'</div><div class=""sub"">Height '+n.blockHeight+' · Diff '+(n.networkDifficulty?f(n.networkDifficulty/1e9,1)+'G':'—')+'</div></div>';
   html+='<div class=""stat""><div class=""top""><span class=""icon"">🔌</span><span class=""label"">'+p.id+' Ports</span></div><div class=""value"" style=""font-size:20px"">'+Object.keys(p.ports||{}).join(', ')+'</div><div class=""sub"">Listening</div></div>';
   html+='<div class=""stat""><div class=""top""><span class=""icon"">💵</span><span class=""label"">Fee</span></div><div class=""value"">'+f(p.poolFeePercent||0,1)+'%</div><div class=""sub"">Pool fee</div></div>';
  }
  html+='</div>';
 }
 html+='<div class=""panel""><div class=""panel-header"">Recent Blocks</div><div class=""panel-body""><table><tr><th>Pool</th><th>Height</th><th>Status</th><th>Effort</th><th>Miner</th><th>When</th></tr>';
 try{
  var blocks=[],seen=new Set();
  for(var p of d.pools||[]){var b=await a('/api/blocks?pool='+p.id+'&state=Confirmed,Pending&pageSize=8');for(var x of b||[]){var k=p.id+'-'+x.blockHeight;if(!seen.has(k)){seen.add(k);blocks.push({...x,pool:p.id});}}}
  blocks.sort((a,b)=>new Date(b.created)-new Date(a.created));
  for(var x of blocks.slice(0,10)){html+='<tr><td>'+x.pool+'</td><td>'+x.blockHeight+'</td><td>'+E(x.status)+'</td><td>'+f(x.effort||0,2)+'%</td><td>'+(x.miner||'—').slice(0,14)+'</td><td>'+ago(x.created)+'</td></tr>';}
 }catch(e){}
 html+='</table></div></div>';
 document.getElementById('content').innerHTML=html;
},
pools:async function(){
 var d=await a('/api/pools'),html='',groups=groupByCoin(d.pools||[]);
 for(var coinName in groups){
 html+='<div style=""margin:18px 0 8px;font-size:13px;font-weight:600;color:var(--muted);text-transform:uppercase;letter-spacing:.5px"">'+coinName+'</div>';
 for(var p of groups[coinName]){var s=p.poolStats||{},n=p.networkStats||{},scheme=p.paymentProcessing&&p.paymentProcessing.payoutScheme;
  html+='<div class=""panel""><div class=""panel-header"">'+p.id+' <span class=""badge badge-ok"">Online</span>'+payoutBadge(scheme)+'</div><div class=""panel-body"">';
  html+='<div class=""stats"" style=""margin-bottom:0"">';
  html+='<div class=""stat""><div class=""label"">Pool Hashrate</div><div class=""value"">'+h(s.poolHashrate||0)+'</div></div>';
  html+='<div class=""stat""><div class=""label"">Connected Miners</div><div class=""value"">'+s.connectedMiners+'</div></div>';
  html+='<div class=""stat""><div class=""label"">Network Hashrate</div><div class=""value"">'+h(n.networkHashrate||0)+'</div></div>';
  html+='<div class=""stat""><div class=""label"">Network Difficulty</div><div class=""value"">'+(n.networkDifficulty?f(n.networkDifficulty/1e9,1)+'G':'—')+'</div></div>';
  html+='<div class=""stat""><div class=""label"">Block Height</div><div class=""value"">'+n.blockHeight+'</div></div>';
  html+='<div class=""stat""><div class=""label"">Ports</div><div class=""value"" style=""font-size:18px"">'+Object.keys(p.ports||{}).join(', ')+'</div></div>';
  html+='</div></div></div>';
 }
 }
 document.getElementById('content').innerHTML=html||'<div class=""loading"">No pools</div>';
},
miners:async function(){
 var html='<div class=""panel""><div class=""panel-header"">Search Miner</div><div class=""panel-body""><div class=""row""><div class=""col""><input id=""s"" placeholder=""Miner address or wallet..."" autofocus></div><div class=""col"" style=""flex:0""><button class=""btn btn-primary"" onclick=""search()"">Search</button></div></div><div id=""r""></div></div></div>';
 var d=await a('/api/pools');
 for(var p of d.pools||[]){
  html+='<div class=""panel""><div class=""panel-header"">'+p.id+' — Live Connections</div><div class=""panel-body""><table><tr><th>Miner</th><th>Worker</th><th>IP</th><th>Diff</th><th>Valid</th><th>Invalid</th><th>Connected</th><th></th></tr>';
  try{
   var conns=await aj('/api/admin/pools/'+p.id+'/connections');
   for(var c of conns||[]){
    html+='<tr><td>'+(c.miner||'—').slice(0,20)+'</td><td>'+(c.worker||'')+'</td><td>'+(c.remoteIp||'')+'</td><td>'+fdiff(c.difficulty)+'</td><td class=""green"">'+c.validShares+'</td><td class=""red"">'+c.invalidShares+'</td><td>'+ago(c.created)+'</td><td><button class=""btn btn-danger"" style=""padding:4px 10px;font-size:11px"" onclick=""banMiner(&quot;'+p.id+'&quot;,&quot;'+c.miner+'&quot;)"">Ban address</button></td></tr>';
   }
   if(!conns||!conns.length) html+='<tr><td colspan=""8"" class=""muted"">No active connections</td></tr>';
  }catch(e){html+='<tr><td colspan=""8"" class=""muted"">Unable to load</td></tr>';}
  html+='</table></div></div>';
 }
 document.getElementById('content').innerHTML=html;
 window.search=async function(){
  var addr=document.getElementById('s').value.trim();if(!addr)return;
  var d=await a('/api/pools'),html='<table><tr><th>Pool</th><th>Hashrate</th><th>Shares/s</th></tr>';
  for(var p of d.pools||[]){
   try{var m=await a('/api/pools/'+p.id+'/miners/'+addr);
    html+='<tr><td>'+p.id+'</td><td>'+h(m.hashrate||0)+'</td><td>'+f(m.sharesPerSecond,1)+'</td></tr>';
   }catch(e){html+='<tr><td>'+p.id+'</td><td colspan=""2"" class=""muted"">Not found</td></tr>';}
  }
  html+='</table>';document.getElementById('r').innerHTML=html||'<p>No data</p>';
 };
 window.banMiner=async function(poolId,address){
  if(!address||address=='undefined')return;
  await pf('/api/admin/pools/'+poolId+'/miner-bans/ban',{address:address});
  loaders.miners();
 };
},
health:async function(){
 var html='<div class=""grid2"">';
 var d=await a('/api/pools');
 html+='<div class=""panel""><div class=""panel-header"">Pools</div><div class=""panel-body""><table><tr><th>Pool</th><th>Daemon</th><th>Height</th><th>Miners</th></tr>';
 for(var p of d.pools||[]){var n=p.networkStats||{},s=p.poolStats||{};
  html+='<tr><td>'+p.id+'</td><td>'+(n.blockHeight?'<span class=""badge badge-ok"">Online</span>':'<span class=""badge badge-warn"">Unknown</span>')+'</td><td>'+(n.blockHeight||'—')+'</td><td>'+(s.connectedMiners||0)+'</td></tr>';
 }
 html+='</table></div></div>';
 html+='<div class=""panel""><div class=""panel-header"">Process</div><div class=""panel-body"">';
 try{var g=await aj('/api/admin/stats/gc');
  html+='<table><tr><th>Gen</th><th>Collections</th></tr><tr><td>Gen 0</td><td>'+g.gcGen0+'</td></tr><tr><td>Gen 1</td><td>'+g.gcGen1+'</td></tr><tr><td>Gen 2</td><td>'+g.gcGen2+'</td></tr></table>';
  html+='<p style=""margin-top:12px""><strong>Managed Memory:</strong> '+g.memAllocated+'</p>';
 }catch(e){html+='<p class=""muted"">Unable to load</p>';}
 html+='</div></div></div>';
 document.getElementById('content').innerHTML=html;
},
bans:async function(){
 var html='<div class=""panel""><div class=""panel-header"">Banned IPs</div><div class=""panel-body""><table><tr><th>Address</th><th>Until</th><th></th></tr>';
 try{
  var bans=await aj('/api/admin/bans');
  for(var b of bans||[]){
   html+='<tr><td>'+b.address+'</td><td>'+(new Date(b.bannedUntil).getFullYear()>9000?'Permanent':ago(b.bannedUntil))+'</td><td><button class=""btn btn-secondary"" style=""padding:4px 10px;font-size:11px"" onclick=""unban(&quot;'+b.address+'&quot;)"">Unban</button></td></tr>';
  }
  if(!bans||!bans.length) html+='<tr><td colspan=""3"" class=""muted"">No active bans</td></tr>';
 }catch(e){html+='<tr><td colspan=""3"" class=""muted"">Unable to load</td></tr>';}
 html+='</table></div></div>';
 document.getElementById('content').innerHTML=html;
 window.unban=async function(addr){await pf('/api/admin/bans/unban',{address:addr});loaders.bans();};
},
farms:async function(){
 // Pure detection, no auto-action - a human decides what to do with a flag.
 // yiimp parity: flags any (pool, address) currently receiving hashrate from
 // more than the threshold's worth of distinct IPs. Computed entirely
 // client-side from data already fetched by the Miners tab
 // (/api/admin/pools/{poolId}/connections) - no new backend endpoint.
 var threshold=parseInt(localStorage.getItem('mcce_farm_threshold')||'10',10);
 var html='<div class=""panel""><div class=""panel-header"">Large Farm / Botnet Detection</div><div class=""panel-body"">';
 html+='<p class=""muted"" style=""margin-bottom:14px"">Flags any payout address currently mining from more distinct IPs than the threshold below. Could be a legitimate large farm, could be malware mining to one address - this only flags, it never bans automatically.</p>';
 html+='<div class=""row""><div class=""col"" style=""flex:0;display:flex;align-items:center;gap:8px""><label style=""font-size:11px;color:var(--muted)"">Flag at more than</label><input id=""farmThreshold"" type=""number"" min=""2"" value=""'+threshold+'"" style=""width:80px"" onchange=""setFarmThreshold()""><label style=""font-size:11px;color:var(--muted)"">distinct IPs</label></div></div>';
 html+='<table style=""margin-top:14px""><tr><th>Pool</th><th>Address</th><th>Distinct IPs</th><th>Workers</th><th></th></tr><tbody id=""farmRows""><tr><td colspan=""5"" class=""loading"">Loading...</td></tr></tbody></table>';
 html+='</div></div>';
 document.getElementById('content').innerHTML=html;

 window.setFarmThreshold=function(){
  var v=parseInt(document.getElementById('farmThreshold').value,10)||10;
  localStorage.setItem('mcce_farm_threshold',v);
  loaders.farms();
 };

 window.banFarmAddress=async function(poolId,address){
  await pf('/api/admin/pools/'+poolId+'/miner-bans/ban',{address:address});
  loaders.farms();
 };

 var d=await a('/api/pools'),rows='',flagged=0;
 for(var p of d.pools||[]){
  try{
   var conns=await aj('/api/admin/pools/'+p.id+'/connections');
   var byAddress={};
   for(var c of conns||[]){
    if(!c.miner)continue;
    var g=byAddress[c.miner]=byAddress[c.miner]||{ips:new Set(),workers:new Set()};
    g.ips.add(c.remoteIp);g.workers.add(c.worker||'');
   }
   for(var addr in byAddress){
    var g=byAddress[addr];
    if(g.ips.size>threshold){
     flagged++;
     rows+='<tr><td>'+p.id+'</td><td>'+addr.slice(0,24)+'</td><td class=""yellow"">'+g.ips.size+'</td><td>'+g.workers.size+'</td><td><button class=""btn btn-danger"" style=""padding:4px 10px;font-size:11px"" onclick=""banFarmAddress(&quot;'+p.id+'&quot;,&quot;'+addr+'&quot;)"">Ban address</button></td></tr>';
    }
   }
  }catch(e){}
 }
 document.getElementById('farmRows').innerHTML=rows||'<tr><td colspan=""5"" class=""muted"">No address over the threshold right now</td></tr>';
},
alerts:async function(){
 var d=await a('/api/pools'),opts='';
 for(var p of d.pools||[]) opts+='<option value=""'+p.id+'"">'+p.id+'</option>';
 var metricOpts='<option value=""poolHashrate"">Pool hashrate</option><option value=""connectedMiners"">Connected miners</option><option value=""networkHashrate"">Network hashrate</option><option value=""networkDifficulty"">Network difficulty</option><option value=""minutesSinceBlockHeightChange"">Minutes since block height changed</option>';
 var opOpts='<option value=""<"">&lt;</option><option value="">"">&gt;</option><option value=""<="">&lt;=</option><option value="">="">&gt;=</option><option value=""=="">==</option>';
 var html='<div class=""panel""><div class=""panel-header"">Active Alerts</div><div class=""panel-body""><table><tr><th>Pool</th><th>Metric</th><th>Condition</th><th>Current</th><th>Description</th></tr><tbody id=""activeAlertRows""><tr><td colspan=""5"" class=""loading"">Loading...</td></tr></tbody></table></div></div>';
 html+='<div class=""panel""><div class=""panel-header"">Alert Rules</div><div class=""panel-body"">';
 html+='<div class=""row""><div class=""col""><select id=""ruleePool"">'+opts+'</select></div><div class=""col""><select id=""ruleMetric"">'+metricOpts+'</select></div><div class=""col"" style=""flex:0""><select id=""ruleOp"" style=""width:70px"">'+opOpts+'</select></div><div class=""col""><input id=""ruleThreshold"" placeholder=""threshold, e.g. 100""></div></div>';
 html+='<div class=""row""><div class=""col""><input id=""ruleDesc"" placeholder=""Description (shown in the alert/email)""></div><div class=""col"" style=""flex:0""><button class=""btn btn-primary"" onclick=""addRule()"">Add rule</button></div></div>';
 html+='<table style=""margin-top:14px""><tr><th>Pool</th><th>Condition</th><th>Last triggered</th><th>Enabled</th><th></th></tr><tbody id=""ruleRows""><tr><td colspan=""5"" class=""loading"">Loading...</td></tr></tbody></table>';
 html+='</div></div>';
 document.getElementById('content').innerHTML=html;

 async function refreshActive(){
  try{
   var active=await aj('/api/admin/alerts/active');
   var rows='';
   for(var al of active||[]) rows+='<tr><td>'+al.poolId+'</td><td>'+al.metric+'</td><td>'+al.operator+' '+al.threshold+'</td><td class=""red"">'+f(al.currentValue,3)+'</td><td>'+(al.description||'')+'</td></tr>';
   document.getElementById('activeAlertRows').innerHTML=rows||'<tr><td colspan=""5"" class=""muted"">No alerts triggered right now</td></tr>';
  }catch(e){document.getElementById('activeAlertRows').innerHTML='<tr><td colspan=""5"" class=""muted"">Unable to load</td></tr>';}
 }

 async function refreshRules(){
  try{
   var rules=await aj('/api/admin/alerts/rules');
   var rows='';
   for(var r of rules||[]){
    rows+='<tr><td>'+r.poolId+'</td><td>'+r.metric+' '+r.operator+' '+r.threshold+'</td><td>'+(r.lastTriggeredAt?ago(r.lastTriggeredAt):'never')+'</td><td>'+(r.enabled?'<span class=""badge badge-ok"">On</span>':'<span class=""badge badge-warn"">Off</span>')+'</td><td><button class=""btn btn-secondary"" style=""padding:4px 10px;font-size:11px"" onclick=""toggleRule(&quot;'+r.id+'&quot;,'+(!r.enabled)+')"">'+(r.enabled?'Disable':'Enable')+'</button> <button class=""btn btn-danger"" style=""padding:4px 10px;font-size:11px"" onclick=""deleteRule(&quot;'+r.id+'&quot;)"">Delete</button></td></tr>';
   }
   document.getElementById('ruleRows').innerHTML=rows||'<tr><td colspan=""5"" class=""muted"">No alert rules configured</td></tr>';
  }catch(e){document.getElementById('ruleRows').innerHTML='<tr><td colspan=""5"" class=""muted"">Unable to load</td></tr>';}
 }

 refreshActive();refreshRules();

 window.addRule=async function(){
  var threshold=parseFloat(document.getElementById('ruleThreshold').value);
  if(isNaN(threshold)){alert('Enter a numeric threshold');return}
  await pf('/api/admin/alerts/rules',{
   poolId:document.getElementById('ruleePool').value,
   metric:document.getElementById('ruleMetric').value,
   operator:document.getElementById('ruleOp').value,
   threshold:threshold,
   description:document.getElementById('ruleDesc').value,
  });
  document.getElementById('ruleThreshold').value='';document.getElementById('ruleDesc').value='';
  refreshRules();
 };
 window.toggleRule=async function(id,enable){await pf('/api/admin/alerts/rules/'+id+'/'+(enable?'enable':'disable'),{});refreshRules();};
 window.deleteRule=async function(id){await pf('/api/admin/alerts/rules/'+id+'/delete',{});refreshRules();refreshActive();};
},
logs:async function(){
 document.getElementById('content').innerHTML='<div class=""panel""><div class=""panel-header"">Recent Log Output<button class=""btn btn-secondary"" style=""padding:4px 10px;font-size:11px"" onclick=""loaders.logs()"">Refresh</button></div><div class=""panel-body""><pre id=""logbox"" style=""white-space:pre-wrap;max-height:600px;overflow-y:auto;font-size:12px;line-height:1.6"">Loading...</pre></div></div>';
 try{
  var lines=await aj('/api/admin/logs?count=300');
  document.getElementById('logbox').textContent=(lines||[]).join(String.fromCharCode(10))||'No log output yet';
 }catch(e){document.getElementById('logbox').textContent='Unable to load logs';}
},
auditlog:async function(){
 var html='<div class=""panel""><div class=""panel-header"">Admin Action Audit Log</div><div class=""panel-body""><table><tr><th>When</th><th>IP</th><th>Action</th><th>Detail</th></tr>';
 try{
  var entries=await aj('/api/admin/auditlog?count=200');
  for(var e of entries||[]){
   html+='<tr><td>'+ago(e.timestamp)+'</td><td>'+e.ip+'</td><td>'+e.action+'</td><td>'+(e.detail||'').slice(0,80)+'</td></tr>';
  }
  if(!entries||!entries.length) html+='<tr><td colspan=""4"" class=""muted"">No admin actions recorded yet</td></tr>';
 }catch(e){html+='<tr><td colspan=""4"" class=""muted"">Unable to load</td></tr>';}
 html+='</table></div></div>';
 document.getElementById('content').innerHTML=html;
},
console:async function(){
 var d=await a('/api/pools'),opts='';
 for(var p of d.pools||[]) opts+='<option value=""'+p.id+'"">'+p.id+'</option>';
 var html='<div class=""panel""><div class=""panel-header"">⚠️ Raw Daemon RPC Console</div><div class=""panel-body"">';
 html+='<p class=""muted"" style=""margin-bottom:14px"">Sends ANY RPC command directly to the selected pool\'s daemon, like bitcoin-cli. There is no command allowlist - a destructive or wallet-draining command will run exactly as typed. Only Bitcoin-family pools support this (see Known limitations in docs/ADMIN_PANEL.md).</p>';
 html+='<div class=""row""><div class=""col""><label style=""font-size:11px;color:var(--muted)"">Pool</label><select id=""rpcPool"">'+opts+'</select></div></div>';
 html+='<div class=""row""><div class=""col""><label style=""font-size:11px;color:var(--muted)"">Method</label><input id=""rpcMethod"" placeholder=""getblockchaininfo""></div></div>';
 html+='<div class=""row""><div class=""col""><label style=""font-size:11px;color:var(--muted)"">Params (JSON array, optional)</label><input id=""rpcParams"" placeholder=""[&quot;some-address&quot;, 6]""></div></div>';
 html+='<button class=""btn btn-danger"" onclick=""runRpc()"">Run</button>';
 html+='<pre id=""rpcOut"" style=""white-space:pre-wrap;margin-top:14px;max-height:400px;overflow-y:auto;font-size:12px;line-height:1.6;display:none""></pre>';
 html+='</div></div>';
 document.getElementById('content').innerHTML=html;
 window.runRpc=async function(){
  var pool=document.getElementById('rpcPool').value;
  var method=document.getElementById('rpcMethod').value.trim();
  var params=document.getElementById('rpcParams').value.trim();
  var out=document.getElementById('rpcOut');
  out.style.display='block';out.textContent='Running...';
  if(!method){out.textContent='Enter a method name first.';return}
  try{
   var r=await pf('/api/admin/pools/'+pool+'/rpc',{method:method,params:params});
   var text=await r.text();
   try{out.textContent=JSON.stringify(JSON.parse(text),null,2)}catch(e){out.textContent=text}
  }catch(e){out.textContent='Error: '+e.message}
 };
},
coins:async function(){
 var algos=[];
 try{algos=await aj('/api/admin/algorithms')}catch(e){}
 var algoOpts=(algos||[]).map(function(x){return '<option value=""'+x+'"">'+x+'</option>'}).join('');

 var html='<div class=""panel""><div class=""panel-header"">Add Coin</div><div class=""panel-body"">';
 html+='<p class=""muted"" style=""margin-bottom:14px"">This is a convenience form over config.json / coins.json - it writes the exact same files you could edit by hand, then the existing hot-reload picks up the change automatically within a few seconds. No restart, no interruption to any other running pool.</p>';
 html+='<div class=""row""><div class=""col""><label><input type=""checkbox"" id=""acExisting"" style=""width:auto;margin-right:8px"">Use an existing coin already in coins.json (e.g. dogecoin, litecoin)</label></div></div>';
 html+='<div class=""row""><div class=""col""><label style=""font-size:11px;color:var(--muted)"">Coin Key</label><input id=""acKey"" placeholder=""e.g. newcoin, or dogecoin if using existing""></div><div class=""col""><label style=""font-size:11px;color:var(--muted)"">Pool ID</label><input id=""acPoolId"" placeholder=""e.g. newcoin-v1""></div></div>';
 html+='<div id=""acNewCoinFields"">';
 html+='<div class=""row""><div class=""col""><label style=""font-size:11px;color:var(--muted)"">Coin Name</label><input id=""acName"" placeholder=""e.g. NewCoin""></div><div class=""col""><label style=""font-size:11px;color:var(--muted)"">Symbol</label><input id=""acSymbol"" placeholder=""e.g. NEW""></div></div>';
 html+='<div class=""row""><div class=""col""><label style=""font-size:11px;color:var(--muted)"">Algorithm</label><select id=""acAlgo"">'+algoOpts+'</select></div><div class=""col""><label style=""font-size:11px;color:var(--muted)"">Algorithm params (optional, comma-separated numbers)</label><input id=""acAlgoArgs"" placeholder=""e.g. 1024,1 for scrypt""></div></div>';
 html+='<div class=""row""><div class=""col""><label style=""font-size:11px;color:var(--muted)"">Website (optional)</label><input id=""acWebsite"" placeholder=""https://...""></div><div class=""col""><label style=""font-size:11px;color:var(--muted)"">Block Explorer URL (optional, {0} = block height/hash placeholder)</label><input id=""acExplorer"" placeholder=""https://explorer.example.com/block/{0}""></div></div>';
 html+='</div>';
 html+='<div class=""row""><div class=""col""><label style=""font-size:11px;color:var(--muted)"">Payout Address</label><input id=""acAddress""></div></div>';
 html+='<div class=""row""><div class=""col""><label style=""font-size:11px;color:var(--muted)"">Pool Fee %</label><input id=""acFee"" type=""number"" step=""any"" value=""0""></div><div class=""col""><label style=""font-size:11px;color:var(--muted)"">Fee Payout Address (your own)</label><input id=""acFeeAddress""></div></div>';
 html+='<div class=""row""><div class=""col""><label style=""font-size:11px;color:var(--muted)"">Daemon Host</label><input id=""acHost""></div><div class=""col""><label style=""font-size:11px;color:var(--muted)"">Daemon Port</label><input id=""acPort"" type=""number""></div></div>';
 html+='<div class=""row""><div class=""col""><label style=""font-size:11px;color:var(--muted)"">Daemon RPC User</label><input id=""acUser""></div><div class=""col""><label style=""font-size:11px;color:var(--muted)"">Daemon RPC Password</label><input id=""acPass"" type=""password""></div></div>';
 html+='<div class=""row""><div class=""col""><label style=""font-size:11px;color:var(--muted)"">Daemon HTTP Path (optional)</label><input id=""acHttpPath"" placeholder=""/wallet/dev""></div></div>';
 html+='<div class=""row""><div class=""col""><label style=""font-size:11px;color:var(--muted)"">Stratum Port</label><input id=""acStratum"" type=""number""></div><div class=""col""><label style=""font-size:11px;color:var(--muted)"">Starting Difficulty</label><input id=""acDiff"" type=""number"" step=""any"" value=""0.001""></div></div>';
 html+='<div class=""row""><div class=""col""><label style=""font-size:11px;color:var(--muted)"">VarDiff Min (blank = auto)</label><input id=""acVdMin"" type=""number"" step=""any""></div><div class=""col""><label style=""font-size:11px;color:var(--muted)"">VarDiff Max (blank = unlimited)</label><input id=""acVdMax"" type=""number"" step=""any""></div></div>';
 html+='<div class=""row""><div class=""col""><label style=""font-size:11px;color:var(--muted)"">Mining Mode</label><select id=""acMode""><option value=""shared"">Shared (PPLNS)</option><option value=""solo"">Solo</option></select></div></div>';
 html+='<div class=""row""><div class=""col""><label><input type=""checkbox"" id=""acPaymentsOn"" style=""width:auto;margin-right:8px"">Enable automatic payouts</label></div></div>';
 html+='<div class=""row""><div class=""col""><label style=""font-size:11px;color:var(--muted)"">Minimum Payment</label><input id=""acMinPay"" type=""number"" step=""any"" value=""0.5""></div><div class=""col"" id=""acFactorCol""><label style=""font-size:11px;color:var(--muted)"">PPLNS Factor</label><input id=""acFactor"" type=""number"" step=""any"" value=""2.0""></div></div>';
 html+='<button class=""btn btn-primary"" id=""acSubmit"">Add Coin</button> <span id=""acResult"" style=""margin-left:12px""></span>';
 html+='</div></div>';
 html+='<div class=""panel""><div class=""panel-header"">Existing Pools</div><div class=""panel-body""><table><tr><th>Pool</th><th>Coin</th><th>Port</th><th>Fee</th><th>Address</th><th></th><th></th></tr><tbody id=""managePoolRows""><tr><td colspan=""7"" class=""loading"">Loading...</td></tr></tbody></table></div></div>';
 html+='<div id=""editPoolContainer""></div>';
 document.getElementById('content').innerHTML=html;

 function syncMode(){document.getElementById('acFactorCol').style.display=document.getElementById('acMode').value==='solo'?'none':''}
 document.getElementById('acMode').addEventListener('change',syncMode);syncMode();
 function syncExisting(){document.getElementById('acNewCoinFields').style.display=document.getElementById('acExisting').checked?'none':''}
 document.getElementById('acExisting').addEventListener('change',syncExisting);syncExisting();

 document.getElementById('acSubmit').addEventListener('click',async function(){
  var argsRaw=document.getElementById('acAlgoArgs').value.trim();
  var body={
   useExistingCoin:document.getElementById('acExisting').checked,
   coinKey:document.getElementById('acKey').value.trim(),
   poolId:document.getElementById('acPoolId').value.trim(),
   coinName:document.getElementById('acName').value.trim(),
   coinSymbol:document.getElementById('acSymbol').value.trim(),
   algorithm:document.getElementById('acAlgo').value,
   algorithmArgs:argsRaw?argsRaw.split(',').map(function(x){return parseInt(x.trim(),10)}):null,
   website:document.getElementById('acWebsite').value.trim(),
   explorerBlockLink:document.getElementById('acExplorer').value.trim(),
   address:document.getElementById('acAddress').value.trim(),
   daemonHost:document.getElementById('acHost').value.trim(),
   daemonPort:parseInt(document.getElementById('acPort').value||'0',10),
   daemonUser:document.getElementById('acUser').value,
   daemonPassword:document.getElementById('acPass').value,
   daemonHttpPath:document.getElementById('acHttpPath').value.trim(),
   stratumPort:parseInt(document.getElementById('acStratum').value||'0',10),
   difficulty:parseFloat(document.getElementById('acDiff').value||'0.001'),
   solo:document.getElementById('acMode').value==='solo',
   enablePaymentProcessing:document.getElementById('acPaymentsOn').checked,
   minimumPayment:parseFloat(document.getElementById('acMinPay').value||'0.5'),
   pplnsFactor:parseFloat(document.getElementById('acFactor').value||'2.0'),
   feePercent:parseFloat(document.getElementById('acFee').value||'0'),
   feeAddress:document.getElementById('acFeeAddress').value.trim(),
   varDiffMinDiff:document.getElementById('acVdMin').value?parseFloat(document.getElementById('acVdMin').value):null,
   varDiffMaxDiff:document.getElementById('acVdMax').value?parseFloat(document.getElementById('acVdMax').value):null,
  };
  var res=document.getElementById('acResult');
  res.textContent='Submitting...';res.className='';
  try{
   var r=await pj('/api/admin/coins/add',body);
   var text=await r.text();var msg=text;
   try{var j=JSON.parse(text);msg=j.message||j.error||text}catch(e){}
   res.textContent=msg;res.className=r.ok?'green':'red';
   if(r.ok)loadManagePools();
  }catch(e){res.textContent='Request failed: '+e;res.className='red'}
 });

 async function loadManagePools(){
  var list=[];
  try{list=await aj('/api/admin/coins/list')}catch(e){}
  var rows='';
  for(var p of list||[]){
   var scheme=!p.enablePaymentProcessing?'<span class=""badge"">Off</span>':payoutBadge(p.solo?'SOLO':'PPLNS');
   rows+='<tr><td>'+(p.enabled?'🟢':'🔴')+' '+p.poolId+'</td><td>'+p.coinKey+'</td><td>'+p.stratumPort+'</td><td>'+(p.feePercent||0)+'%'+scheme+'</td><td>'+(p.address||'').slice(0,20)+'</td><td><button class=""btn btn-secondary"" style=""padding:4px 10px;font-size:11px"" onclick=""editPool(&quot;'+p.poolId+'&quot;)"">Edit</button></td><td><button class=""btn btn-danger"" style=""padding:4px 10px;font-size:11px"" onclick=""removePool(&quot;'+p.poolId+'&quot;)"">Remove</button></td></tr>';
  }
  document.getElementById('managePoolRows').innerHTML=rows||'<tr><td colspan=""7"" class=""muted"">No pools configured</td></tr>';
  window.__poolList=list||[];
 }
 loadManagePools();

 window.removePool=async function(id){
  if(!confirm('Remove pool \'' + id + '\'? Miners connected to it will be disconnected.'))return;
  await pj('/api/admin/coins/remove',{poolId:id});
  loadManagePools();
 };

 window.editPool=function(id){
  var p=(window.__poolList||[]).find(function(x){return x.poolId===id});
  if(!p)return;
  var c=document.getElementById('editPoolContainer');
  var h='<div class=""panel""><div class=""panel-header"">Edit '+p.poolId+' <span style=""cursor:pointer;color:var(--muted)"" onclick=""document.getElementById(&quot;editPoolContainer&quot;).innerHTML=&quot;&quot;"">✕ close</span></div><div class=""panel-body"">';
  h+='<div class=""row""><div class=""col""><label style=""font-size:11px;color:var(--muted)"">Payout Address</label><input id=""epAddress"" value=""'+(p.address||'')+'""></div></div>';
  h+='<div class=""row""><div class=""col""><label style=""font-size:11px;color:var(--muted)"">Pool Fee %</label><input id=""epFee"" type=""number"" step=""any"" value=""'+(p.feePercent||0)+'""></div><div class=""col""><label style=""font-size:11px;color:var(--muted)"">Fee Payout Address</label><input id=""epFeeAddress"" value=""'+(p.feeAddress||'')+'""></div></div>';
  h+='<div class=""row""><div class=""col""><label style=""font-size:11px;color:var(--muted)"">Coin Name (shared by every pool on this coin key - e.g. renaming here separates/groups it with others on the Overview tab)</label><input id=""epCoinName"" value=""'+(p.coinName||'')+'""></div></div>';
  h+='<div class=""row""><div class=""col""><label style=""font-size:11px;color:var(--muted)"">Block Explorer URL</label><input id=""epExplorer"" value=""'+(p.explorerBlockLink||'')+'""></div></div>';
  h+='<div class=""row""><div class=""col""><label style=""font-size:11px;color:var(--muted)"">Daemon Host</label><input id=""epHost"" value=""'+(p.daemonHost||'')+'""></div><div class=""col""><label style=""font-size:11px;color:var(--muted)"">Daemon Port</label><input id=""epPort"" type=""number"" value=""'+(p.daemonPort||'')+'""></div></div>';
  h+='<div class=""row""><div class=""col""><label style=""font-size:11px;color:var(--muted)"">Daemon RPC User</label><input id=""epUser"" value=""'+(p.daemonUser||'')+'""></div><div class=""col""><label style=""font-size:11px;color:var(--muted)"">Daemon RPC Password (blank = keep current)</label><input id=""epPass"" type=""password""></div></div>';
  h+='<div class=""row""><div class=""col""><label style=""font-size:11px;color:var(--muted)"">Daemon HTTP Path</label><input id=""epHttpPath"" value=""'+(p.daemonHttpPath||'')+'""></div></div>';
  h+='<div class=""row""><div class=""col""><label style=""font-size:11px;color:var(--muted)"">Difficulty</label><input id=""epDiff"" type=""number"" step=""any"" value=""'+(p.difficulty||0.001)+'""></div><div class=""col""><label style=""font-size:11px;color:var(--muted)"">Enabled</label><select id=""epEnabled""><option value=""true""'+(p.enabled?' selected':'')+'>Enabled</option><option value=""false""'+(!p.enabled?' selected':'')+'>Disabled</option></select></div></div>';
  h+='<div class=""row""><div class=""col""><label style=""font-size:11px;color:var(--muted)"">Mining Mode</label><select id=""epMode""><option value=""shared""'+(!p.solo?' selected':'')+'>Shared (PPLNS)</option><option value=""solo""'+(p.solo?' selected':'')+'>Solo</option></select></div></div>';
  h+='<div class=""row""><div class=""col""><label><input type=""checkbox"" id=""epPaymentsOn"" style=""width:auto;margin-right:8px""'+(p.enablePaymentProcessing?' checked':'')+'>Enable automatic payouts</label></div></div>';
  h+='<div class=""row""><div class=""col""><label style=""font-size:11px;color:var(--muted)"">Minimum Payment</label><input id=""epMinPay"" type=""number"" step=""any"" value=""'+(p.minimumPayment||0.5)+'""></div><div class=""col""><label style=""font-size:11px;color:var(--muted)"">PPLNS Factor</label><input id=""epFactor"" type=""number"" step=""any"" value=""2.0""></div></div>';
  h+='<button class=""btn btn-primary"" onclick=""saveEditPool(&quot;'+p.poolId+'&quot;)"">Save Changes</button> <span id=""epResult"" style=""margin-left:12px""></span>';
  h+='</div></div>';
  c.innerHTML=h;
  c.scrollIntoView({behavior:'smooth'});
 };

 window.saveEditPool=async function(poolId){
  var p=(window.__poolList||[]).find(function(x){return x.poolId===poolId});
  var body={
   poolId:poolId,
   address:document.getElementById('epAddress').value.trim(),
   daemonHost:document.getElementById('epHost').value.trim(),
   daemonPort:parseInt(document.getElementById('epPort').value||'0',10),
   daemonUser:document.getElementById('epUser').value,
   daemonPassword:document.getElementById('epPass').value||'',
   daemonHttpPath:document.getElementById('epHttpPath').value.trim(),
   difficulty:parseFloat(document.getElementById('epDiff').value||'0.001'),
   enabled:document.getElementById('epEnabled').value==='true',
   solo:document.getElementById('epMode').value==='solo',
   enablePaymentProcessing:document.getElementById('epPaymentsOn').checked,
   minimumPayment:parseFloat(document.getElementById('epMinPay').value||'0.5'),
   pplnsFactor:parseFloat(document.getElementById('epFactor').value||'2.0'),
   feePercent:parseFloat(document.getElementById('epFee').value||'0'),
   feeAddress:document.getElementById('epFeeAddress').value.trim(),
   coinName:document.getElementById('epCoinName').value.trim(),
   explorerBlockLink:document.getElementById('epExplorer').value.trim(),
   varDiffMinDiff:p&&p.varDiffMinDiff!=null?p.varDiffMinDiff:null,
   varDiffMaxDiff:p&&p.varDiffMaxDiff!=null?p.varDiffMaxDiff:null,
   varDiffTargetTime:p?p.varDiffTargetTime:15,
   varDiffRetargetTime:p?p.varDiffRetargetTime:90,
   varDiffVariancePercent:p?p.varDiffVariancePercent:30,
  };
  var res=document.getElementById('epResult');
  res.textContent='Saving...';res.className='';
  try{
   var r=await pj('/api/admin/coins/edit',body);
   var text=await r.text();var msg=text;
   try{var j=JSON.parse(text);msg=j.message||j.error||text}catch(e){}
   res.textContent=msg;res.className=r.ok?'green':'red';
   if(r.ok)loadManagePools();
  }catch(e){res.textContent='Request failed: '+e;res.className='red'}
 };
},
blocks:async function(){
 var d=await a('/api/pools'),html='';
 for(var p of d.pools||[]){
  try{
   var b=await a('/api/blocks?pool='+p.id+'&state=Confirmed,Pending,Orphaned&pageSize=15');
   html+='<div class=""panel""><div class=""panel-header"">'+p.id+' <span style=""color:var(--muted);font-weight:400;font-size:12px"">last 15</span></div><div class=""panel-body""><table><tr><th>Height</th><th>Status</th><th>Effort</th><th>Miner</th><th>Reward</th><th>When</th></tr>';
   for(var x of b||[]){html+='<tr><td>'+(x.infoLink?'<a href=""'+x.infoLink+'"" target=""_blank"" style=""color:#58a6ff"">'+x.blockHeight+'</a>':x.blockHeight)+'</td><td>'+E(x.status)+'</td><td>'+f(x.effort||0,2)+'%</td><td>'+(x.miner||'—').slice(0,16)+'</td><td>'+f(x.reward||0,6)+'</td><td>'+ago(x.created)+'</td></tr>';}
   html+='</table></div></div>';
  }catch(e){}
 }
 document.getElementById('content').innerHTML=html||'<div class=""loading"">No blocks found</div>';
},
payments:async function(){
 var d=await a('/api/pools'),html='';
 for(var p of d.pools||[]){
  try{
   var pp=await a('/api/pools/'+p.id+'/payments?pageSize=15');
   html+='<div class=""panel""><div class=""panel-header"">'+p.id+' <span style=""color:var(--muted);font-weight:400;font-size:12px"">last 15</span></div><div class=""panel-body""><table><tr><th>Address</th><th>Amount</th><th>When</th></tr>';
   for(var x of pp||[]){html+='<tr><td>'+(x.address||'—').slice(0,20)+'...</td><td>'+f(x.amount,6)+'</td><td>'+ago(x.created)+'</td></tr>';}
   html+='</table></div></div>';
  }catch(e){}
 }
 document.getElementById('content').innerHTML=html||'<div class=""loading"">No payments</div>';
},
settings:async function(){
 var html='<div class=""grid2"">';
 html+='<div class=""panel""><div class=""panel-header"">Garbage Collection</div><div class=""panel-body"">';
 try{var g=await aj('/api/admin/stats/gc');html+='<table><tr><th>Gen</th><th>Collections</th></tr><tr><td>Gen 0</td><td>'+g.gcGen0+'</td></tr><tr><td>Gen 1</td><td>'+g.gcGen1+'</td></tr><tr><td>Gen 2</td><td>'+g.gcGen2+'</td></tr></table><p style=""margin-top:12px""><strong>Managed Memory:</strong> '+g.memAllocated+'</p><p style=""margin-top:8px""><button class=""btn btn-secondary"" onclick=""forceGC()"">Force GC</button></p>';}catch(e){html+='<p class=""muted"">Unable to load GC stats</p>';}
 html+='</div></div>';
 html+='<div class=""panel""><div class=""panel-header"">System</div><div class=""panel-body""><table>';
 try{var d=await a('/api/pools');var n=d.pools[0].networkStats||{};
  html+='<tr><td>Coin</td><td>'+(d.pools[0].coin?.name||'—')+'</td></tr>';
  html+='<tr><td>Difficulty</td><td>'+(n.networkDifficulty?f(n.networkDifficulty/1e9,1)+'G':'—')+'</td></tr>';
  html+='<tr><td>Block Height</td><td>'+n.blockHeight+'</td></tr>';
 }catch(e){}
 html+='<tr><td>Build</td><td>MCCE .NET 9</td></tr>';
 html+='</table></div></div>';
 html+='<div class=""panel""><div class=""panel-header"">Payouts</div><div class=""panel-body""><p class=""desc"" style=""color:var(--muted);margin-bottom:12px;font-size:12px"">Triggers an immediate payout cycle across all pools, same logic as the periodic timer.</p><button class=""btn btn-primary"" onclick=""runPayoutNow()"">Run Payout Cycle Now</button><p id=""payoutResult"" style=""margin-top:10px;font-size:12px;color:var(--muted)""></p></div></div>';
 html+='<div class=""panel""><div class=""panel-header"">Manual Balance Adjustment</div><div class=""panel-body"">';
 html+='<div class=""row""><div class=""col""><input id=""adjPool"" placeholder=""Pool ID""></div><div class=""col""><input id=""adjAddr"" placeholder=""Miner address""></div></div>';
 html+='<div class=""row""><div class=""col""><input id=""adjAmount"" placeholder=""Amount (negative to debit)""></div><div class=""col""><input id=""adjReason"" placeholder=""Reason (required, goes in audit log)""></div></div>';
 html+='<button class=""btn btn-primary"" onclick=""adjustBalance()"">Apply</button><p id=""adjResult"" style=""margin-top:10px;font-size:12px;color:var(--muted)""></p>';
 html+='</div></div>';
 html+='<div class=""panel""><div class=""panel-header"">Admin Panel Password</div><div class=""panel-body"">';
 html+='<div class=""row""><div class=""col""><input id=""pwCurrent"" type=""password"" placeholder=""Current password""></div><div class=""col""><input id=""pwNext"" type=""password"" placeholder=""New password""></div></div>';
 html+='<button class=""btn btn-secondary"" onclick=""changePassword()"">Change Password</button><p id=""pwResult"" style=""margin-top:10px;font-size:12px;color:var(--muted)""></p>';
 html+='</div></div>';
 html+='<div class=""panel""><div class=""panel-header"">Minimum Confirmations (per pool)</div><div class=""panel-body"">';
 html+='<p class=""desc"" style=""color:var(--muted);margin-bottom:12px;font-size:12px"">Changes only the *displayed* confirmation-progress percentage for immature blocks. Real payout eligibility always comes from the daemon\'s own coinbase maturity rule, never from this. In-memory only - reverts to config.json on restart. Leave blank and Save to reset to the configured default.</p>';
 html+='<div class=""row""><div class=""col""><select id=""mcPool""></select></div><div class=""col"" style=""flex:0 0 140px""><input id=""mcValue"" type=""number"" min=""1"" placeholder=""confirmations""></div><div class=""col"" style=""flex:0""><button class=""btn btn-primary"" onclick=""saveMinConfirmations()"">Save</button></div></div>';
 html+='<p id=""mcResult"" style=""margin-top:10px;font-size:12px;color:var(--muted)""></p>';
 html+='</div></div>';
 html+='</div>';
 document.getElementById('content').innerHTML=html;

 try{
  var pd=await a('/api/pools');
  document.getElementById('mcPool').innerHTML=(pd.pools||[]).map(function(p){return '<option value=""'+p.id+'"">'+p.id+'</option>'}).join('');
 }catch(e){}

 window.runPayoutNow=async function(){
  document.getElementById('payoutResult').textContent='Running...';
  try{await pf('/api/admin/payouts/run',{});document.getElementById('payoutResult').textContent='Payout cycle completed.';}
  catch(e){document.getElementById('payoutResult').textContent='Failed: '+e.message;}
 };
 window.adjustBalance=async function(){
  var pool=document.getElementById('adjPool').value.trim();
  var addr=document.getElementById('adjAddr').value.trim();
  var amount=document.getElementById('adjAmount').value.trim();
  var reason=document.getElementById('adjReason').value.trim();
  if(!pool||!addr||!amount||!reason){document.getElementById('adjResult').textContent='All fields are required.';return;}
  try{
   var r=await pf('/api/admin/pools/'+pool+'/miners/'+addr+'/balance/adjust',{amount:amount,reason:reason});
   var body=await r.json();
   document.getElementById('adjResult').textContent='New balance: '+body.newBalance;
  }catch(e){document.getElementById('adjResult').textContent='Failed: '+e.message;}
 };
 window.changePassword=async function(){
  var current=document.getElementById('pwCurrent').value;
  var next=document.getElementById('pwNext').value;
  if(!next){document.getElementById('pwResult').textContent='New password cannot be empty.';return;}
  try{
   await pf('/admin/change-password',{current:current,next:next});
   document.getElementById('pwResult').textContent='Password changed. You will need to log in again.';
  }catch(e){document.getElementById('pwResult').textContent='Failed: '+e.message;}
 };
 window.forceGC=function(){
  fetch('/api/admin/forcegc',{method:'POST'}).then(function(r){alert(r.ok?'GC triggered':'Failed')});
 };
 window.saveMinConfirmations=async function(){
  var pool=document.getElementById('mcPool').value;
  var value=document.getElementById('mcValue').value.trim();
  try{
   await pf('/api/admin/pools/'+pool+'/min-confirmations',{value:value});
   document.getElementById('mcResult').textContent=value?'Saved: '+pool+' now uses '+value+' confirmations.':'Reset '+pool+' to its configured default.';
  }catch(e){document.getElementById('mcResult').textContent='Failed: '+e.message;}
 };
}
};
loaders.overview();
</script></body></html>";
}
