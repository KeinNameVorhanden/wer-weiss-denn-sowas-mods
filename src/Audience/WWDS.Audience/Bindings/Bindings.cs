using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using WWDS.ModCore.Serialization;
using WWDS.ModCore.Model;

namespace WWDS.Audience.Bindings;

/// <summary>One rule: when event <see cref="On"/> occurs and all <see cref="When"/> conditions hold, run <see cref="Do"/>.</summary>
public sealed class BindingRule
{
    public string Id { get; set; } = string.Empty;

    /// <summary>Event class name, e.g. "ChannelPointRedeemed", "CheerReceived", "AnswersPresented".</summary>
    public string On { get; set; } = string.Empty;

    /// <summary>Property path (dot separated) to condition: "=x", "~part", "&gt;=500", "&lt;3", or a plain value (equals, case-insensitive).</summary>
    public Dictionary<string, string> When { get; set; } = new();

    /// <summary>Action name registered by a feature, e.g. "naming.open".</summary>
    public string Do { get; set; } = string.Empty;

    /// <summary>Action arguments; "{Path}" placeholders are filled from the event.</summary>
    public Dictionary<string, string> Args { get; set; } = new();

    /// <summary>Seconds before the rule may fire again (0 = no limit).</summary>
    public double Cooldown { get; set; }

    public bool Enabled { get; set; } = true;
}

public sealed class BindingFile
{
    public List<BindingRule> Rules { get; set; } = new();
}

public delegate ActionResult BindingAction(IDictionary<string, string> args, object evt);

/// <summary>Named actions features expose to bindings (and to the overlay's test buttons).</summary>
public sealed class BindingActions
{
    private readonly Dictionary<string, BindingAction> _actions = new(StringComparer.OrdinalIgnoreCase);

    public IEnumerable<string> Names => _actions.Keys.OrderBy(k => k);

    public IDisposable Register(string name, BindingAction action)
    {
        _actions[name] = action;
        return new Handle(() =>
        {
            if (_actions.TryGetValue(name, out var a) && a == action) _actions.Remove(name);
        });
    }

    public ActionResult Run(string name, IDictionary<string, string> args, object evt) =>
        _actions.TryGetValue(name, out var a) ? a(args, evt) : ActionResult.Fail("unknown action '" + name + "'");

    private sealed class Handle : IDisposable
    {
        private Action? _dispose;

        public Handle(Action dispose) => _dispose = dispose;

        public void Dispose()
        {
            _dispose?.Invoke();
            _dispose = null;
        }
    }
}

/// <summary>Evaluates rules against events. Pure: no game, no Twitch, testable with plain objects.</summary>
public sealed class BindingEngine
{
    private readonly List<BindingRule> _rules = new();
    private readonly Dictionary<string, double> _lastFired = new();
    private readonly BindingActions _actions;

    public BindingEngine(BindingActions actions) => _actions = actions;

    public IList<BindingRule> Rules => _rules.AsReadOnly();

    public static BindingFile Parse(string json) => Json.Deserialize<BindingFile>(json) ?? new BindingFile();

    public void Load(IEnumerable<BindingRule> rules)
    {
        _rules.Clear();
        _lastFired.Clear();
        var n = 0;
        foreach (var r in rules)
        {
            n++;
            if (r.Id.Length == 0) r.Id = "rule" + n;
            _rules.Add(r);
        }
    }

    /// <summary>Runs every matching rule; returns (rule id, result) for logging.</summary>
    public IList<KeyValuePair<string, ActionResult>> Handle(object evt, double now)
    {
        var fired = new List<KeyValuePair<string, ActionResult>>();
        var typeName = evt.GetType().Name;
        foreach (var rule in _rules)
        {
            if (!rule.Enabled || !string.Equals(rule.On, typeName, StringComparison.OrdinalIgnoreCase)) continue;
            if (!Matches(rule, evt)) continue;
            if (rule.Cooldown > 0 && _lastFired.TryGetValue(rule.Id, out var last) && now - last < rule.Cooldown) continue;
            _lastFired[rule.Id] = now;
            var args = rule.Args.ToDictionary(kv => kv.Key, kv => Fill(kv.Value, evt), StringComparer.OrdinalIgnoreCase);
            ActionResult result;
            try
            {
                result = _actions.Run(rule.Do, args, evt);
            }
            catch (Exception ex)
            {
                result = ActionResult.Fail(ex.GetType().Name + ": " + ex.Message);
            }

            fired.Add(new KeyValuePair<string, ActionResult>(rule.Id, result));
        }

        return fired;
    }

    public static bool Matches(BindingRule rule, object evt)
    {
        foreach (var kv in rule.When)
        {
            var value = Resolve(evt, kv.Key);
            if (!Compare(value, kv.Value)) return false;
        }

        return true;
    }

    internal static bool Compare(object? actual, string condition)
    {
        var c = condition.Trim();
        string op;
        if (c.StartsWith(">=", StringComparison.Ordinal) || c.StartsWith("<=", StringComparison.Ordinal) || c.StartsWith("!=", StringComparison.Ordinal)) op = c.Substring(0, 2);
        else if (c.StartsWith(">", StringComparison.Ordinal) || c.StartsWith("<", StringComparison.Ordinal) || c.StartsWith("=", StringComparison.Ordinal) || c.StartsWith("~", StringComparison.Ordinal)) op = c.Substring(0, 1);
        else op = string.Empty;
        var expected = c.Substring(op.Length).Trim();
        var text = actual == null ? string.Empty : Convert.ToString(actual, CultureInfo.InvariantCulture) ?? string.Empty;

        if (op is ">" or ">=" or "<" or "<=")
        {
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var a) || !double.TryParse(expected, NumberStyles.Float, CultureInfo.InvariantCulture, out var b)) return false;
            return op switch
            {
                ">" => a > b,
                ">=" => a >= b,
                "<" => a < b,
                _ => a <= b,
            };
        }

        if (op == "~") return text.IndexOf(expected, StringComparison.OrdinalIgnoreCase) >= 0;
        var equal = string.Equals(text, expected, StringComparison.OrdinalIgnoreCase);
        return op == "!=" ? !equal : equal;
    }

    /// <summary>Reads a dot-separated property path ("Viewer.DisplayName") via reflection.</summary>
    internal static object? Resolve(object? target, string path)
    {
        foreach (var part in path.Split('.'))
        {
            if (target == null) return null;
            var prop = target.GetType().GetProperty(part, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (prop == null) return null;
            target = prop.GetValue(target, null);
        }

        return target;
    }

    internal static string Fill(string template, object evt)
    {
        if (template.IndexOf('{') < 0) return template;
        var sb = new StringBuilder();
        var i = 0;
        while (i < template.Length)
        {
            var open = template.IndexOf('{', i);
            var close = open < 0 ? -1 : template.IndexOf('}', open);
            if (open < 0 || close < 0)
            {
                sb.Append(template.Substring(i));
                break;
            }

            sb.Append(template, i, open - i);
            var value = Resolve(evt, template.Substring(open + 1, close - open - 1));
            sb.Append(value == null ? string.Empty : Convert.ToString(value, CultureInfo.InvariantCulture));
            i = close + 1;
        }

        return sb.ToString();
    }

    /// <summary>Example rules written on first start (disabled where they need streamer-specific names).</summary>
    public static string ExampleJson() =>
        "{\n" +
        "  \"rules\": [\n" +
        "    { \"id\": \"redeem-name-opponent\", \"on\": \"ChannelPointRedeemed\", \"when\": { \"RewardTitle\": \"Name the Opponent\" }, \"do\": \"naming.open\", \"args\": { \"target\": \"opponent\", \"seconds\": \"45\" }, \"cooldown\": 60 },\n" +
        "    { \"id\": \"redeem-crowd\", \"on\": \"ChannelPointRedeemed\", \"when\": { \"RewardTitle\": \"Chat spielt mit\" }, \"do\": \"opponent.enable\" },\n" +
        "    { \"id\": \"sub-thanks\", \"on\": \"SubscriptionReceived\", \"do\": \"chat.say\", \"args\": { \"text\": \"Danke fuer das Abo, {Viewer.DisplayName}!\" }, \"enabled\": false },\n" +
        "    { \"id\": \"gift-bomb-rename\", \"on\": \"SubscriptionReceived\", \"when\": { \"GiftCount\": \">=5\" }, \"do\": \"naming.open\", \"args\": { \"target\": \"opponent\", \"seconds\": \"40\" } },\n" +
        "    { \"id\": \"big-cheer-hybrid\", \"on\": \"CheerReceived\", \"when\": { \"Bits\": \">=500\" }, \"do\": \"opponent.mode\", \"args\": { \"mode\": \"Hybrid\" } },\n" +
        "    { \"id\": \"raid-opponent\", \"on\": \"RaidReceived\", \"when\": { \"Viewers\": \">=20\" }, \"do\": \"opponent.enable\" },\n" +
        "    { \"id\": \"question-vote\", \"on\": \"AnswersPresented\", \"do\": \"vote.open\", \"enabled\": false },\n" +
        "    { \"id\": \"poll-answer\", \"on\": \"PollUpdated\", \"when\": { \"Status\": \"end\" }, \"do\": \"chat.say\", \"args\": { \"text\": \"Umfrage beendet: {Title}\" }, \"enabled\": false }\n" +
        "  ]\n" +
        "}\n";

    public static BindingFile LoadOrCreate(string path)
    {
        if (!File.Exists(path)) File.WriteAllText(path, ExampleJson(), new UTF8Encoding(false));
        return Parse(File.ReadAllText(path));
    }
}
