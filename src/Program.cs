using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

var publishedWebRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
var contentRoot = Directory.Exists(publishedWebRoot) ? AppContext.BaseDirectory : Directory.GetCurrentDirectory();
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = contentRoot });
builder.WebHost.UseUrls("http://127.0.0.1:5178");
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
});
builder.Services.AddSingleton(new HttpClient { Timeout = TimeSpan.FromMinutes(4) });
builder.Services.AddSingleton<SessionStore>();
builder.Services.AddSingleton<OllamaClient>();
builder.Services.AddSingleton<AgentOrchestrator>();
builder.Services.AddSingleton<RunRegistry>();

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/status", async (OllamaClient ollama, CancellationToken ct) =>
{
    try
    {
        var models = await ollama.ListModelsAsync(ct);
        return Results.Ok(new { ok = true, provider = "Ollama · 本机", endpoint = ollama.DisplayEndpoint, models });
    }
    catch (Exception ex)
    {
        return Results.Ok(new { ok = false, provider = "Ollama · 本机", endpoint = ollama.DisplayEndpoint, models = Array.Empty<string>(), error = ex.Message });
    }
});

app.MapGet("/api/sessions", async (SessionStore store, RunRegistry runs, CancellationToken ct) => Results.Ok(await store.ListAsync(runs.IsRunning, ct)));
app.MapPost("/api/sessions", async (CreateSessionRequest request, SessionStore store, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.Goal)) return Results.BadRequest(new { error = "先写清楚这次要完成什么。" });
    var session = await store.CreateAsync(request.Goal.Trim(), request.Model?.Trim(), ct);
    return Results.Created($"/api/sessions/{session.Id}", session);
});
app.MapGet("/api/sessions/{id}", async (string id, SessionStore store, RunRegistry runs, CancellationToken ct) =>
{
    var session = await store.GetAsync(id, ct);
    if (session is not null) await store.RecoverInterruptedAsync(session, runs.IsRunning, ct);
    return session is null ? Results.NotFound() : Results.Ok(session);
});
app.MapPost("/api/sessions/{id}/run", async (string id, SessionStore store, RunRegistry runs, AgentOrchestrator orchestrator, CancellationToken ct) =>
{
    var session = await store.GetAsync(id, ct);
    if (session is null) return Results.NotFound();
    if (session.Status is SessionStatus.AwaitingReview or SessionStatus.Approved)
        return Results.Conflict(new { error = "本轮已经交给你审核；通过后请新建会话，要求修改则从审查阶段继续。" });
    if (!runs.TryStart(id, out var runCts)) return Results.Conflict(new { error = "这个会话已有任务在运行。" });
    session.Status = SessionStatus.Running;
    session.Error = null;
    session.UpdatedAt = DateTimeOffset.Now;
    await store.SaveAsync(session, ct);
    _ = Task.Run(async () =>
    {
        try { await orchestrator.RunAsync(session, runCts.Token); }
        catch (OperationCanceledException) { session.Status = SessionStatus.Cancelled; session.UpdatedAt = DateTimeOffset.Now; await store.SaveAsync(session); }
        catch (Exception ex) { session.Status = SessionStatus.Failed; session.Error = ex.Message; session.UpdatedAt = DateTimeOffset.Now; await store.SaveAsync(session); }
        finally { runs.Finish(id); }
    });
    return Results.Accepted($"/api/sessions/{id}", new { session.Id, session.Status });
});
app.MapPost("/api/sessions/{id}/cancel", (string id, RunRegistry runs) =>
    runs.Cancel(id) ? Results.Ok(new { ok = true }) : Results.Conflict(new { error = "会话当前没有运行中的任务。" }));
app.MapPost("/api/sessions/{id}/decision", async (string id, DecisionRequest request, SessionStore store, RunRegistry runs, CancellationToken ct) =>
{
    if (runs.IsRunning(id)) return Results.Conflict(new { error = "运行中不能审核，请先等待或停止。" });
    var session = await store.GetAsync(id, ct);
    if (session is null) return Results.NotFound();
    if (request.Decision is not ("approve" or "revise")) return Results.BadRequest(new { error = "decision 只能是 approve 或 revise。" });
    session.HumanDecision = request.Decision;
    session.DecisionNote = request.Note?.Trim();
    session.Status = request.Decision == "approve" ? SessionStatus.Approved : SessionStatus.RevisionRequested;
    if (request.Decision == "revise") session.CurrentStage = "reviewer";
    session.UpdatedAt = DateTimeOffset.Now;
    session.Timeline.Add(new SessionEvent { Kind = "human-review", Role = "你", Text = request.Decision == "approve" ? "已通过本轮审核。" : $"要求修改：{request.Note}" });
    await store.SaveAsync(session, ct);
    return Results.Ok(session);
});
app.MapPost("/api/sessions/{id}/talk", async (string id, TalkRequest request, SessionStore store, RunRegistry runs, AgentOrchestrator orchestrator, CancellationToken ct) =>
{
    if (runs.IsRunning(id)) return Results.Conflict(new { error = "工作流运行期间暂不能插入对话。" });
    if (string.IsNullOrWhiteSpace(request.Message)) return Results.BadRequest(new { error = "消息不能为空。" });
    var session = await store.GetAsync(id, ct);
    if (session is null) return Results.NotFound();
    var agent = AgentRole.FromKey(request.Role);
    if (agent is null) return Results.BadRequest(new { error = "未知代理角色。" });
    try
    {
        await orchestrator.TalkAsync(session, agent, request.Message.Trim(), ct);
        return Results.Ok(session);
    }
    catch (Exception ex) { return Results.Problem(ex.Message); }
});

app.MapGet("/api/health", () => Results.Ok(new { ok = true, product = "AgentHub", localOnly = true }));
app.Run();

record CreateSessionRequest(string Goal, string? Model);
record DecisionRequest(string Decision, string? Note);
record TalkRequest(string Role, string Message);

enum SessionStatus { Draft, Running, AwaitingReview, Approved, RevisionRequested, Cancelled, Failed }

sealed class WorkSession
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "新任务";
    public string Goal { get; set; } = "";
    public string? Model { get; set; }
    public SessionStatus Status { get; set; } = SessionStatus.Draft;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
    public List<SessionEvent> Timeline { get; set; } = [];
    public string? HumanDecision { get; set; }
    public string? DecisionNote { get; set; }
    public string? Error { get; set; }
    public string CurrentStage { get; set; } = "planner";
    public List<WorkItem> WorkItems { get; set; } = [];
}

sealed class WorkItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "";
    public string AssigneeRole { get; set; } = "specialist";
    public string Description { get; set; } = "";
    public string Acceptance { get; set; } = "";
    public string Status { get; set; } = "pending";
    public string? Result { get; set; }
}

sealed class SessionEvent
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset At { get; set; } = DateTimeOffset.Now;
    public string Kind { get; set; } = "agent";
    public string Role { get; set; } = "";
    public string RoleKey { get; set; } = "";
    public string Text { get; set; } = "";
}

sealed record AgentRole(string Key, string Name, string Badge, string SystemPrompt)
{
    public static AgentRole Planner { get; } = new("planner", "规划师", "拆解目标", "你是任务规划师。把目标拆成 2 至 4 个可验证的工作项，仅分配给 analyst 或 specialist。返回严格 JSON 对象，不要 Markdown：{\"summary\":\"目标概述\",\"tasks\":[{\"title\":\"短标题\",\"assignee\":\"analyst 或 specialist\",\"description\":\"工作说明\",\"acceptance\":\"完成标准\"}]}。至少给 analyst 和 specialist 各分配一个任务。不可声称工作已完成。");
    public static AgentRole Analyst { get; } = new("analyst", "分析师", "独立分析", "你是独立分析师。你负责规划中的背景分析、证据需求、约束和风险项。只能依据用户提供的信息；不要假装已经联网检索。明确已知、假设和需要验证的点。只输出中文。");
    public static AgentRole Specialist { get; } = new("specialist", "执行专家", "独立方案", "你是负责解决用户任务的专家。依据目标和任务拆分，给出具体、可执行的解决方案。指出假设与风险；没有真实执行的工作不能声称已完成。只输出中文。");
    public static AgentRole Reviewer { get; } = new("reviewer", "审查员", "独立复核", "你是独立质量审查员。不要迎合前一位代理。逐项核验目标覆盖、事实依据、可执行性、风险和遗漏；给出通过/需修改判断和理由。你只评审，不替别人执行。只输出中文。");
    public static AgentRole Synthesizer { get; } = new("synthesizer", "协调员", "结论汇总", "你是协调员。综合用户目标、规划、专家方案和独立审查意见，给出清晰最终答复：结论、下一步、未解决项。若审查指出关键缺陷，必须明确保留为待修订，不能伪装成通过。只输出中文。");
    public static IReadOnlyList<AgentRole> Pipeline { get; } = [Planner, Analyst, Specialist, Reviewer, Synthesizer];
    public static AgentRole? FromKey(string? key) => Pipeline.FirstOrDefault(r => r.Key == key);
}

sealed class SessionStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true, Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };
    private readonly string _root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentHub", "sessions");
    private readonly ConcurrentDictionary<string, WorkSession> _cache = new();
    private readonly SemaphoreSlim _diskLock = new(1, 1);

    public async Task<WorkSession> CreateAsync(string goal, string? model, CancellationToken ct)
    {
        var title = goal.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "新任务";
        if (title.Length > 46) title = title[..46] + "…";
        var s = new WorkSession { Title = title, Goal = goal, Model = model };
        s.Timeline.Add(new SessionEvent { Kind = "user", Role = "你", Text = goal });
        _cache[s.Id] = s;
        await SaveAsync(s, ct);
        return s;
    }

    public async Task<WorkSession?> GetAsync(string id, CancellationToken ct)
    {
        if (_cache.TryGetValue(id, out var cached)) return cached;
        var path = Path.Combine(_root, id + ".json");
        if (!File.Exists(path)) return null;
        try
        {
            var s = JsonSerializer.Deserialize<WorkSession>(await File.ReadAllTextAsync(path, ct), Json);
            if (s is not null) _cache[id] = s;
            return s;
        }
        catch { return null; }
    }

    public async Task<IReadOnlyList<WorkSession>> ListAsync(Func<string, bool> isRunning, CancellationToken ct)
    {
        Directory.CreateDirectory(_root);
        foreach (var path in Directory.EnumerateFiles(_root, "*.json"))
        {
            var id = Path.GetFileNameWithoutExtension(path);
            if (!_cache.ContainsKey(id)) await GetAsync(id, ct);
        }
        foreach (var session in _cache.Values) await RecoverInterruptedAsync(session, isRunning, ct);
        return _cache.Values.OrderByDescending(s => s.UpdatedAt).ToArray();
    }

    public async Task RecoverInterruptedAsync(WorkSession s, Func<string, bool> isRunning, CancellationToken ct)
    {
        if (s.Status != SessionStatus.Running || isRunning(s.Id)) return;
        s.Status = SessionStatus.Cancelled;
        s.Error = "应用上次在代理运行期间关闭；可以从当前检查点继续。";
        foreach (var item in s.WorkItems.Where(w => w.Status == "running")) item.Status = "pending";
        s.Timeline.Add(new SessionEvent { Kind = "system", Role = "协作台", Text = s.Error });
        await SaveAsync(s, ct);
    }

    public async Task SaveAsync(WorkSession s, CancellationToken ct = default)
    {
        s.UpdatedAt = DateTimeOffset.Now;
        _cache[s.Id] = s;
        Directory.CreateDirectory(_root);
        await _diskLock.WaitAsync(ct);
        try
        {
            var path = Path.Combine(_root, s.Id + ".json");
            var temp = path + ".tmp";
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(s, Json), ct);
            File.Move(temp, path, true);
        }
        finally { _diskLock.Release(); }
    }
}

sealed class RunRegistry
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _runs = new();
    public bool TryStart(string id, out CancellationTokenSource cts)
    {
        cts = new CancellationTokenSource();
        if (_runs.TryAdd(id, cts)) return true;
        cts.Dispose();
        return false;
    }
    public bool IsRunning(string id) => _runs.ContainsKey(id);
    public bool Cancel(string id) => _runs.TryGetValue(id, out var cts) && CancelCore(cts);
    private static bool CancelCore(CancellationTokenSource cts) { cts.Cancel(); return true; }
    public void Finish(string id) { if (_runs.TryRemove(id, out var cts)) cts.Dispose(); }
}

sealed class OllamaClient(HttpClient http)
{
    private readonly string _endpoint = Environment.GetEnvironmentVariable("AGENTHUB_OLLAMA_URL") ?? "http://127.0.0.1:11434";
    public string DisplayEndpoint => _endpoint;
    private Uri BaseUri
    {
        get
        {
            if (!Uri.TryCreate(_endpoint, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttp || !uri.IsLoopback)
                throw new InvalidOperationException("为了保证本地运行，AGENTHUB_OLLAMA_URL 必须是 127.0.0.1 或 localhost 的 HTTP 地址。");
            return uri;
        }
    }
    public async Task<string[]> ListModelsAsync(CancellationToken ct)
    {
        using var response = await http.GetAsync(new Uri(BaseUri, "/api/tags"), ct);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return doc.RootElement.GetProperty("models").EnumerateArray().Select(x => x.GetProperty("name").GetString()!).Where(x => x is not null).ToArray();
    }
    public async Task<string> ChatAsync(string model, string system, IReadOnlyList<(string Role, string Content)> messages, CancellationToken ct, string? format = null)
    {
        if (string.IsNullOrWhiteSpace(model)) throw new InvalidOperationException("本机没有已安装的 Ollama 模型。");
        var allMessages = new List<object> { new { role = "system", content = system } };
        allMessages.AddRange(messages.Select(m => (object)new { role = m.Role, content = m.Content }));
        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["messages"] = allMessages,
            ["stream"] = false,
            ["options"] = new { temperature = 0.25, num_ctx = 4096, num_predict = 900 }
        };
        if (format is not null) payload["format"] = format;
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(BaseUri, "/api/chat")) { Content = JsonContent.Create(payload) };
        using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException($"本机 Ollama 返回 {(int)res.StatusCode}: {body}");
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("message").GetProperty("content").GetString()?.Trim() ?? "（模型未返回内容）";
    }
}

sealed class AgentOrchestrator(OllamaClient ollama, SessionStore store)
{
    private static readonly SemaphoreSlim GpuQueue = new(1, 1);
    public async Task RunAsync(WorkSession s, CancellationToken ct)
    {
        var model = s.Model ?? (await ollama.ListModelsAsync(ct)).FirstOrDefault() ?? throw new InvalidOperationException("先在本机 Ollama 安装一个模型。");
        s.Model = model;
        var results = s.Timeline.Where(e => e.Kind == "agent" && !string.IsNullOrWhiteSpace(e.RoleKey))
            .GroupBy(e => e.RoleKey).ToDictionary(g => g.Key, g => g.Last().Text);
        var start = Math.Max(0, AgentRole.Pipeline.ToList().FindIndex(r => r.Key == s.CurrentStage));
        for (var i = start; i < AgentRole.Pipeline.Count; i++)
        {
            var role = AgentRole.Pipeline[i];
            ct.ThrowIfCancellationRequested();
            var context = role.Key switch
            {
                "planner" => $"用户目标：\n{s.Goal}\n\n请开始拆分。",
                "analyst" => $"用户目标：\n{s.Goal}\n\n你的明确分工：\n{FormatAssignments(s, "analyst")}\n\n请完成这些工作项。",
                "specialist" => $"用户目标：\n{s.Goal}\n\n你的明确分工：\n{FormatAssignments(s, "specialist")}\n\n分析师的输入：\n{results.GetValueOrDefault("analyst", "尚未生成")}\n\n请完成这些工作项。",
                "reviewer" => $"原始目标：\n{s.Goal}\n\n规划：\n{results.GetValueOrDefault("planner", "尚未生成")}\n\n分析：\n{results.GetValueOrDefault("analyst", "尚未生成")}\n\n专家方案：\n{results.GetValueOrDefault("specialist", "尚未生成")}\n\n用户补充审核意见：\n{s.DecisionNote ?? "无"}\n\n请独立审查。",
                _ => $"用户目标：\n{s.Goal}\n\n规划：\n{results.GetValueOrDefault("planner", "尚未生成")}\n\n分析：\n{results.GetValueOrDefault("analyst", "尚未生成")}\n\n专家方案：\n{results.GetValueOrDefault("specialist", "尚未生成")}\n\n独立审查：\n{results.GetValueOrDefault("reviewer", "尚未生成")}\n\n请汇总。"
            };
            foreach (var item in s.WorkItems.Where(w => w.AssigneeRole == role.Key && w.Status != "completed")) item.Status = "running";
            await store.SaveAsync(s);
            var answer = await QueuedChatAsync(model, role.SystemPrompt, [("user", context)], ct, role.Key == "planner" ? "json" : null);
            if (role.Key == "planner")
            {
                s.WorkItems = ParsePlan(answer);
                if (s.WorkItems.Count == 0)
                    s.WorkItems = [
                        new WorkItem { Title = "分析背景和风险", AssigneeRole = "analyst", Description = "列出目标的假设、限制和风险。", Acceptance = "已区分事实、假设和待确认项。" },
                        new WorkItem { Title = "完成核心方案", AssigneeRole = "specialist", Description = s.Goal, Acceptance = "方案覆盖用户目标并包含可执行步骤。" }
                    ];
            }
            foreach (var item in s.WorkItems.Where(w => w.AssigneeRole == role.Key && w.Status == "running")) { item.Status = "completed"; item.Result = answer; }
            results[role.Key] = answer;
            s.Timeline.Add(new SessionEvent { Kind = "agent", Role = role.Name, RoleKey = role.Key, Text = answer });
            s.CurrentStage = i + 1 < AgentRole.Pipeline.Count ? AgentRole.Pipeline[i + 1].Key : "complete";
            await store.SaveAsync(s);
        }
        s.Status = SessionStatus.AwaitingReview;
        s.HumanDecision = null;
        s.Timeline.Add(new SessionEvent { Kind = "system", Role = "协作台", Text = "多角色流程完成，等待你作最终审核。" });
        await store.SaveAsync(s, ct);
    }

    public async Task TalkAsync(WorkSession s, AgentRole role, string message, CancellationToken ct)
    {
        var model = s.Model ?? (await ollama.ListModelsAsync(ct)).FirstOrDefault() ?? throw new InvalidOperationException("本机 Ollama 没有可用模型。");
        s.Model = model;
        s.Timeline.Add(new SessionEvent { Kind = "user", Role = "你", Text = message });
        await store.SaveAsync(s, ct);
        var thread = s.Timeline.Where(e => e.RoleKey == role.Key || e.Kind == "user")
            .TakeLast(12).Select(e => (e.Kind == "user" ? "user" : "assistant", e.Text)).ToList();
        var answer = await QueuedChatAsync(model, role.SystemPrompt + $"\n\n这是一项长期工作会话。原始目标：{s.Goal}", thread, ct);
        s.Timeline.Add(new SessionEvent { Kind = "agent", Role = role.Name, RoleKey = role.Key, Text = answer });
        await store.SaveAsync(s, ct);
    }

    private async Task<string> QueuedChatAsync(string model, string system, IReadOnlyList<(string Role, string Content)> messages, CancellationToken ct, string? format = null)
    {
        await GpuQueue.WaitAsync(ct);
        try { return await ollama.ChatAsync(model, system, messages, ct, format); }
        finally { GpuQueue.Release(); }
    }

    private static string FormatAssignments(WorkSession s, string role) =>
        string.Join("\n", s.WorkItems.Where(w => w.AssigneeRole == role).Select((w, i) => $"{i + 1}. {w.Title}\n说明：{w.Description}\n完成标准：{w.Acceptance}"));

    private static List<WorkItem> ParsePlan(string raw)
    {
        try
        {
            var start = raw.IndexOf('{'); var end = raw.LastIndexOf('}');
            if (start < 0 || end <= start) return [];
            using var doc = JsonDocument.Parse(raw[start..(end + 1)]);
            if (!doc.RootElement.TryGetProperty("tasks", out var tasks) || tasks.ValueKind != JsonValueKind.Array) return [];
            var result = new List<WorkItem>();
            foreach (var t in tasks.EnumerateArray())
            {
                var assignee = t.TryGetProperty("assignee", out var a) ? a.GetString()?.ToLowerInvariant() : null;
                if (assignee is not ("analyst" or "specialist")) continue;
                result.Add(new WorkItem
                {
                    Title = t.TryGetProperty("title", out var title) ? title.GetString() ?? "工作项" : "工作项",
                    AssigneeRole = assignee,
                    Description = t.TryGetProperty("description", out var description) ? description.GetString() ?? "" : "",
                    Acceptance = t.TryGetProperty("acceptance", out var acceptance) ? acceptance.GetString() ?? "" : ""
                });
            }
            return result.Take(6).ToList();
        }
        catch { return []; }
    }
}
