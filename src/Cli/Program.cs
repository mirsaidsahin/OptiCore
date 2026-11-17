using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OptiCore.Engine;
using OptiCore.Engine.Platform;

namespace OptiCore.Cli;

/// <summary>
/// Command line front end for the engine.
///   opticore-cli serve [--port 5317] [--ui dir] [--dry-run] [--data-dir dir]   development server for the web UI
///   opticore-cli call method [json-params] [--dry-run] [--lang en]             invoke any engine method, print JSON
///   opticore-cli methods                                                       list engine methods
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Console.WriteLine("OptiCore CLI\n  serve [--port N] [--ui DIR] [--dry-run] [--data-dir DIR]\n  call METHOD [JSON] [--dry-run] [--lang tr|en] [--wait]\n  methods");
            return 0;
        }

        string Opt(string name, string fallback)
        {
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
        }
        bool Flag(string name) => args.Contains(name);

        var options = new EngineOptions
        {
            DryRun = Flag("--dry-run"),
            DataDir = Opt("--data-dir", "") is { Length: > 0 } dir ? dir : null,
            Host = "browser",
        };

        switch (args[0])
        {
            case "serve":
            {
                if (Elevation.IsElevated)
                {
                    Console.Error.WriteLine("Refusing to start: the development server must not run elevated (it would expose an administrator API on localhost).");
                    return 2;
                }
                using var engine = new OptiCoreEngine(options);
                engine.Start();
                int port = int.Parse(Opt("--port", "5317"));
                string ui = Path.GetFullPath(Opt("--ui", FindUi()));
                await new DevServer(engine, ui, port).RunAsync();
                return 0;
            }
            case "methods":
            {
                using var engine = new OptiCoreEngine(options);
                foreach (var m in engine.Rpc.Methods.OrderBy(x => x)) Console.WriteLine(m);
                return 0;
            }
            case "call" when args.Length >= 2:
            {
                using var engine = new OptiCoreEngine(options);
                engine.Start();
                string method = args[1];
                string parameters = ParseParams(args.Skip(2).TakeWhile(a => !a.StartsWith("--")).ToArray());
                string lang = Opt("--lang", "tr");
                // --warm N: let the samplers collect for N milliseconds first (rates need two samples).
                if (int.TryParse(Opt("--warm", "0"), out int warm) && warm > 0)
                {
                    engine.Metrics.Touch();
                    engine.ProcessSampler.Snapshot();
                    await Task.Delay(warm);
                }
                string response = await engine.Rpc.HandleAsync($"{{\"id\":1,\"method\":{JsonSerializer.Serialize(method)},\"params\":{parameters},\"lang\":\"{lang}\"}}");

                // --wait: when the method started a job, poll it to completion and print the job instead.
                if (Flag("--wait"))
                {
                    using var doc = JsonDocument.Parse(response);
                    if (doc.RootElement.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object
                        && result.TryGetProperty("id", out var id) && result.TryGetProperty("state", out _))
                    {
                        while (true)
                        {
                            await Task.Delay(400);
                            response = await engine.Rpc.HandleAsync($"{{\"id\":2,\"method\":\"jobs.get\",\"params\":{{\"id\":{JsonSerializer.Serialize(id.GetString())}}},\"lang\":\"{lang}\"}}");
                            using var poll = JsonDocument.Parse(response);
                            if (poll.RootElement.GetProperty("result").GetProperty("state").GetString() != "running") break;
                        }
                    }
                }
                using var pretty = JsonDocument.Parse(response);
                Console.WriteLine(JsonSerializer.Serialize(pretty.RootElement, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
                return pretty.RootElement.GetProperty("ok").GetBoolean() ? 0 : 1;
            }
            default:
                Console.Error.WriteLine("Unknown command. Run with --help.");
                return 1;
        }
    }

    /// <summary>Accepts either one JSON object or shell-friendly pairs: n=5 tree=true name=chrome.exe ids=[a,b]</summary>
    private static string ParseParams(string[] parts)
    {
        if (parts.Length == 0) return "{}";
        if (parts[0].TrimStart().StartsWith('{')) return string.Join(" ", parts);
        var obj = new Dictionary<string, object?>();
        foreach (var part in parts)
        {
            int eq = part.IndexOf('=');
            if (eq <= 0) continue;
            string key = part[..eq], value = part[(eq + 1)..];
            obj[key] = value switch
            {
                "true" => true,
                "false" => false,
                "null" => null,
                _ when value.StartsWith('[') && value.EndsWith(']') => value[1..^1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                _ when double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number) => number,
                _ => value,
            };
        }
        return JsonSerializer.Serialize(obj);
    }

    /// <summary>Finds the UI folder: next to the executable when deployed, src/App/ui in a source checkout.</summary>
    private static string FindUi()
    {
        var local = Path.Combine(AppContext.BaseDirectory, "ui");
        if (Directory.Exists(local)) return local;
        for (var dir = new DirectoryInfo(Environment.CurrentDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "App", "ui");
            if (Directory.Exists(candidate)) return candidate;
        }
        return local;
    }
}

/// <summary>
/// Local development server: static UI files plus the RPC endpoint. It only listens on the loopback
/// address, demands a per-run token on every call and rejects foreign Host/Origin headers, so a web
/// page open in the user's browser cannot reach the engine.
/// </summary>
internal sealed class DevServer(OptiCoreEngine engine, string uiRoot, int port)
{
    private readonly string _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));

    private static readonly Dictionary<string, string> Mime = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8", [".js"] = "text/javascript; charset=utf-8", [".mjs"] = "text/javascript; charset=utf-8",
        [".css"] = "text/css; charset=utf-8", [".json"] = "application/json; charset=utf-8", [".svg"] = "image/svg+xml",
        [".png"] = "image/png", [".ico"] = "image/x-icon", [".woff2"] = "font/woff2", [".txt"] = "text/plain; charset=utf-8",
    };

    public async Task RunAsync()
    {
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();
        Console.WriteLine($"OptiCore dev server: http://127.0.0.1:{port}/  (ui: {uiRoot}, dry-run: {engine.Options.DryRun})");
        while (listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await listener.GetContextAsync(); }
            catch (HttpListenerException) { break; }
            _ = Task.Run(() => HandleAsync(ctx));
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var res = ctx.Response;
        try
        {
            res.Headers["Cache-Control"] = "no-store";
            res.Headers["X-Content-Type-Options"] = "nosniff";
            string host = req.Headers["Host"] ?? "";
            if (host != $"127.0.0.1:{port}" && host != $"localhost:{port}") { await Text(res, 403, "forbidden host"); return; }

            string path = req.Url?.AbsolutePath ?? "/";
            if (path == "/api/rpc")
            {
                string? origin = req.Headers["Origin"];
                if (req.HttpMethod != "POST" || req.Headers["X-OptiCore-Token"] != _token
                    || (origin is not null && origin != $"http://127.0.0.1:{port}" && origin != $"http://localhost:{port}"))
                {
                    await Text(res, 403, "forbidden");
                    return;
                }
                using var reader = new StreamReader(req.InputStream, Encoding.UTF8);
                string body = await reader.ReadToEndAsync();
                string json = await engine.Rpc.HandleAsync(body);
                await Send(res, 200, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(json));
                return;
            }

            if (req.HttpMethod != "GET") { await Text(res, 405, "method not allowed"); return; }
            if (path == "/") path = "/index.html";
            string file = Path.GetFullPath(Path.Combine(uiRoot, Uri.UnescapeDataString(path.TrimStart('/')).Replace('/', Path.DirectorySeparatorChar)));
            if (!file.StartsWith(uiRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(file)) { await Text(res, 404, "not found"); return; }

            byte[] bytes = await File.ReadAllBytesAsync(file);
            if (path == "/index.html")
                bytes = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace("<!--opticore-token-->", $"<meta name=\"opticore-token\" content=\"{_token}\">"));
            await Send(res, 200, Mime.GetValueOrDefault(Path.GetExtension(file), "application/octet-stream"), bytes);
        }
        catch (Exception e)
        {
            try { await Text(res, 500, e.Message); } catch { /* client went away */ }
        }
    }

    private static Task Text(HttpListenerResponse res, int status, string text) => Send(res, status, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(text));

    private static async Task Send(HttpListenerResponse res, int status, string contentType, byte[] body)
    {
        res.StatusCode = status;
        res.ContentType = contentType;
        res.ContentLength64 = body.Length;
        await res.OutputStream.WriteAsync(body);
        res.Close();
    }
}
