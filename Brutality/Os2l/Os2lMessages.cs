using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Brutality.Os2l;

/// <summary>
/// OS2L's messages: JSON objects sent back to back over TCP with nothing between them, as VirtualDJ sends them.
/// </summary>
static class Os2lMessages
{
    /// <summary>
    /// Takes every complete object off the front of <paramref name="buffer"/>, by matching braces (ignoring any in
    /// strings), leaving an incomplete one there for more to arrive. Anything between objects, such as newlines some
    /// senders add, is skipped.
    /// </summary>
    public static List<string> Split(StringBuilder buffer)
    {
        var objects = new List<string>();
        var depth = 0;
        var start = -1;
        var inString = false;
        var escaped = false;
        var consumed = 0;

        for (var i = 0; i < buffer.Length; i++)
        {
            var c = buffer[i];
            if (depth == 0)
            {
                if (c == '{')
                {
                    start = i;
                    depth = 1;
                }
                else
                    consumed = i + 1;
                continue;
            }

            if (inString)
            {
                if (escaped)
                    escaped = false;
                else if (c == '\\')
                    escaped = true;
                else if (c == '"')
                    inString = false;
                continue;
            }

            if (c == '"')
                inString = true;
            else if (c == '{')
                depth++;
            else if (c == '}' && --depth == 0)
            {
                objects.Add(buffer.ToString(start, i - start + 1));
                consumed = i + 1;
            }
        }

        buffer.Remove(0, consumed);
        return objects;
    }

    /// <summary>
    /// A beat: <paramref name="position"/> counts beats, a multiple of four on each bar's first beat;
    /// <paramref name="change"/> says the count or tempo jumped (another song, a seek) rather than ticking on.
    /// </summary>
    public static string Beat(bool change, long position, double bpm) =>
        $"{{\"evt\":\"beat\",\"change\":{(change ? "true" : "false")},\"pos\":{position},\"bpm\":{Number(bpm)}}}";

    public static string Button(string name, bool on) =>
        new JsonObject { ["evt"] = "btn", ["name"] = name, ["state"] = on ? "on" : "off" }.ToJsonString();

    /// <summary>
    /// The answer to a subscription (an extension VirtualDJ and SoundSwitch use): what a trigger is now.
    /// </summary>
    public static string Subscribed(string trigger, JsonNode? value) =>
        new JsonObject { ["evt"] = "subscribed", ["trigger"] = trigger, ["value"] = value?.DeepClone() }.ToJsonString();

    /// <summary>
    /// A number as JSON, to a sensible precision, whole numbers without a point.
    /// </summary>
    public static string Number(double value)
    {
        if (!double.IsFinite(value))
            return "0";
        var rounded = Math.Round(value, 4);
        return rounded == Math.Floor(rounded) && Math.Abs(rounded) < 1e15
            ? ((long)rounded).ToString(CultureInfo.InvariantCulture)
            : rounded.ToString("0.####", CultureInfo.InvariantCulture);
    }
}
