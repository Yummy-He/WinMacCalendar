using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace WinMacCalendar;

public sealed record CloudCalendar(string Url, string Name, bool Writable, string? Color = null)
{
    public override string ToString() => Name + (Writable ? "" : "（只读）");
}
public sealed record Account(string Email, string Secret, List<CloudCalendar> Calendars)
{
    public string ScopeFor(CloudCalendar calendar) => Email.ToLowerInvariant() + "|" + calendar.Url;
    public static Account? Load(string json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.TryGetProperty("Calendars", out _)) return root.Deserialize<Account>();
        return new(root.GetProperty("Email").GetString()!, root.GetProperty("Secret").GetString()!,
            [root.GetProperty("Calendar").Deserialize<CloudCalendar>()!]);
    }
    public static string Protect(string password) => Convert.ToBase64String(ProtectedData.Protect(
        Encoding.UTF8.GetBytes(password), null, DataProtectionScope.CurrentUser));
    public string Password() => Encoding.UTF8.GetString(ProtectedData.Unprotect(
        Convert.FromBase64String(Secret), null, DataProtectionScope.CurrentUser));
}

public sealed class ICloud : IDisposable
{
    private static readonly XNamespace D = "DAV:", C = "urn:ietf:params:xml:ns:caldav", Apple = "http://apple.com/ns/ical/";
    private static readonly Uri Root = new("https://caldav.icloud.com/");
    private readonly HttpClient http;
    private readonly AuthenticationHeaderValue auth;
    public ICloud(string email, string password, HttpMessageHandler? handler = null)
    {
        http = new(handler ?? new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(25) };
        auth = new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(email + ":" + password)));
    }
    private static Uri Resolve(Uri parent, string href)
    {
        var uri = new Uri(parent, href);
        if (uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || uri.UserInfo.Length != 0)
            throw new InvalidDataException("日历服务地址须使用标准 HTTPS 且不能包含登录信息");
        string host = uri.IdnHost;
        if (!InDomain(host, "icloud.com") && !InDomain(host, "icloud.com.cn"))
            throw new InvalidDataException($"日历服务器域名不受支持（{host}）");
        return uri;
    }
    private static bool InDomain(string host, string domain) =>
        host.Equals(domain, StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);
    private async Task<HttpResponseMessage> Send(string method, Uri uri, CancellationToken ct, string? body = null,
        string? depth = null, string? etag = null, bool create = false)
    {
        for (int hop = 0; hop < 5; hop++)
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), Resolve(Root, uri.AbsoluteUri));
            request.Headers.Authorization = auth;
            if (depth != null) request.Headers.Add("Depth", depth);
            if (create) request.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Any);
            else if (!string.IsNullOrEmpty(etag)) request.Headers.IfMatch.Add(EntityTagHeaderValue.Parse(etag));
            if (body != null) request.Content = new StringContent(body, Encoding.UTF8, method == "PUT" ? "text/calendar" : "application/xml");
            var response = await http.SendAsync(request, ct);
            if ((int)response.StatusCode is 301 or 302 or 307 or 308 && response.Headers.Location is { } location)
            {
                response.Dispose();
                uri = Resolve(uri, location.ToString());
                continue;
            }
            return response;
        }
        throw new HttpRequestException("iCloud 重定向次数过多");
    }
    private static bool Status(XElement? element, string code) => element?.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1) == code;
    private static IEnumerable<XElement> Props(XElement row) => row.Elements(D + "propstat")
        .Where(p => Status(p.Element(D + "status"), "200")).Elements(D + "prop").Elements();
    private async Task<(Uri Base, XElement Xml)> Xml(string verb, Uri uri, string? depth, XElement body, CancellationToken ct)
    {
        using var response = await Send(verb, uri, ct, body.ToString(), depth);
        response.EnsureSuccessStatusCode();
        return (response.RequestMessage?.RequestUri ?? uri, XElement.Parse(await response.Content.ReadAsStringAsync(ct)));
    }
    private static XElement Find(params XName[] props) => new(D + "propfind", new XElement(D + "prop", props.Select(p => new XElement(p))));
    private async Task<Uri> DiscoverProperty(Uri uri, XName name, CancellationToken ct)
    {
        var (origin, xml) = await Xml("PROPFIND", uri, "0", Find(name), ct);
        var value = xml.Elements(D + "response").SelectMany(Props).FirstOrDefault(p => p.Name == name)?.Element(D + "href")?.Value;
        return Resolve(origin, value ?? throw new InvalidDataException("iCloud 未返回日历位置"));
    }
    public async Task<List<CloudCalendar>> Discover(CancellationToken ct)
    {
        var principal = await DiscoverProperty(Root, D + "current-user-principal", ct);
        var home = await DiscoverProperty(principal, C + "calendar-home-set", ct);
        var (origin, xml) = await Xml("PROPFIND", home, "1", Find(D + "resourcetype", D + "displayname",
            D + "current-user-privilege-set", C + "supported-calendar-component-set", Apple + "calendar-color"), ct);
        var list = new List<CloudCalendar>();
        foreach (var row in xml.Elements(D + "response"))
        {
            var props = Props(row).ToList();
            if (!props.Elements(C + "calendar").Any()) continue;
            var types = props.Elements(C + "comp").ToList();
            if (types.Count > 0 && !types.Any(x => (string?)x.Attribute("name") == "VEVENT")) continue;
            var url = Resolve(origin, (string)row.Element(D + "href")!).AbsoluteUri.TrimEnd('/') + "/";
            var privileges = props.Descendants(D + "privilege").Elements().Select(x => x.Name).ToHashSet();
            var writable = privileges.Contains(D + "all") || privileges.Contains(D + "write") ||
                (privileges.Contains(D + "write-content") && privileges.Contains(D + "bind") && privileges.Contains(D + "unbind"));
            list.Add(new(url, props.FirstOrDefault(p => p.Name == D + "displayname")?.Value ?? "iCloud 日历", writable,
                props.FirstOrDefault(p => p.Name == Apple + "calendar-color")?.Value));
        }
        return list;
    }

    private static Uri Member(Uri collection, string href)
    {
        var uri = Resolve(collection, href);
        if (!collection.IsBaseOf(uri) || uri.AbsoluteUri == collection.AbsoluteUri) throw new InvalidDataException("事件地址超出所选日历");
        return uri;
    }

    private async Task Push(CalendarStore db, string scope, CloudCalendar calendar, Resource item, CancellationToken ct)
    {
        var uri = Member(new Uri(calendar.Url), item.Href);
        bool deleting = item.Pending == "delete";
        if (deleting && item.Etag.Length == 0)
        {
            // A create may have reached iCloud even if its response was lost.
            using var lookup = await Send("GET", uri, ct);
            if (lookup.StatusCode == HttpStatusCode.NotFound) { db.Remove(scope, item.Href); return; }
            lookup.EnsureSuccessStatusCode();
            var canonical = await lookup.Content.ReadAsStringAsync(ct);
            var version = lookup.Headers.ETag?.ToString() ?? throw new InvalidDataException("事件缺少 ETag");
            if (!Events.SameEventVersion(item.Ics, canonical))
            {
                db.Save(scope, new(item.Href, canonical, version));
                return;
            }
            item = item with { Etag = version };
            db.Save(scope, item);
        }
        if (!deleting && item.Etag.Length == 0 && item.Pending == "put")
            db.Save(scope, item with { Pending = "creating" });
        using var result = await Send(deleting ? "DELETE" : "PUT", uri, ct, deleting ? null : item.Ics,
            etag: item.Etag, create: !deleting && item.Etag.Length == 0);
        if (deleting && (result.IsSuccessStatusCode || result.StatusCode == HttpStatusCode.NotFound))
        {
            db.Remove(scope, item.Href);
            return;
        }
        if (result.StatusCode != HttpStatusCode.PreconditionFailed && result.StatusCode != HttpStatusCode.NotFound)
            result.EnsureSuccessStatusCode();
        // Read back the canonical copy after a successful upload or an ETag conflict.
        using var current = await Send("GET", uri, ct);
        if (current.StatusCode == HttpStatusCode.NotFound) { db.Remove(scope, item.Href); return; }
        current.EnsureSuccessStatusCode();
        string tag = current.Headers.ETag?.ToString() ?? throw new InvalidDataException("事件缺少 ETag");
        db.Save(scope, new(item.Href, await current.Content.ReadAsStringAsync(ct), tag));
    }
    public async Task Sync(CalendarStore db, Account account, CloudCalendar calendar, CancellationToken ct)
    {
        var scope = account.ScopeFor(calendar);
        var collection = Resolve(Root, calendar.Url);
        foreach (var item in db.List(scope).Where(i => i.Pending != "")) await Push(db, scope, calendar, item, ct);
        string token = db.Get("token:" + scope);
        XElement Report(string cursor) => new XElement(D + "sync-collection", new XElement(D + "sync-token", cursor),
            new XElement(D + "sync-level", 1), new XElement(D + "prop", new XElement(D + "getetag")));
        using var first = await Send("REPORT", collection, ct, Report(token).ToString(), "0");
        string content = await first.Content.ReadAsStringAsync(ct);
        if (token.Length > 0 && first.StatusCode == HttpStatusCode.Forbidden && XElement.Parse(content).Descendants(D + "valid-sync-token").Any())
        {
            db.Set("token:" + scope, "");
            await Sync(db, account, calendar, ct);
            return;
        }
        first.EnsureSuccessStatusCode();
        var xml = XElement.Parse(content);
        string nextToken;
        var pages = new HashSet<string> { token };
        var versions = new Dictionary<string, string?>();
        while (true)
        {
            nextToken = (string?)xml.Element(D + "sync-token") ?? throw new InvalidDataException("iCloud 缺少同步标记");
            bool more = false;
            foreach (var row in xml.Elements(D + "response"))
            {
                var raw = (string)row.Element(D + "href")!;
                if (Resolve(collection, raw).AbsoluteUri.TrimEnd('/') == collection.AbsoluteUri.TrimEnd('/'))
                {
                    more |= Status(row.Element(D + "status"), "507");
                    continue;
                }
                var href = Member(collection, raw).AbsoluteUri;
                versions[href] = Status(row.Element(D + "status"), "404") ? null :
                    Props(row).FirstOrDefault(p => p.Name == D + "getetag")?.Value ?? throw new InvalidDataException("日程版本读取失败");
            }
            if (!more) break;
            if (!pages.Add(nextToken)) throw new InvalidDataException("日历分页标记重复");
            (_, xml) = await Xml("REPORT", collection, "0", Report(nextToken), ct);
        }
        var local = db.List(scope).ToDictionary(i => i.Href);
        var changed = new List<Resource>();
        var removed = versions.Where(p => p.Value == null).Select(p => p.Key).ToHashSet();
        var needed = versions.Where(p => p.Value != null && (!local.TryGetValue(p.Key, out var entry) || entry.Etag != p.Value)).Select(p => p.Key);
        foreach (var group in needed.Chunk(64))
        {
            var ask = new XElement(C + "calendar-multiget", new XElement(D + "prop", new XElement(D + "getetag"), new XElement(C + "calendar-data")),
                group.Select(h => new XElement(D + "href", new Uri(h).PathAndQuery)));
            var (_, data) = await Xml("REPORT", collection, null, ask, ct);
            var waiting = group.ToHashSet();
            foreach (var row in data.Elements(D + "response"))
            {
                var href = Member(collection, (string)row.Element(D + "href")!).AbsoluteUri;
                if (!waiting.Remove(href)) throw new InvalidDataException("日历响应地址不匹配");
                if (Status(row.Element(D + "status"), "404")) { removed.Add(href); continue; }
                var props = Props(row).ToList();
                changed.Add(new(href, props.First(p => p.Name == C + "calendar-data").Value, props.First(p => p.Name == D + "getetag").Value));
            }
            if (waiting.Count != 0) throw new InvalidDataException("日历下载不完整，将在下次同步时重试");
        }
        if (token == "") removed.UnionWith(local.Keys.Except(versions.Keys));
        db.Apply(scope, changed, removed, nextToken);
    }
    public void Dispose() => http.Dispose();
}
