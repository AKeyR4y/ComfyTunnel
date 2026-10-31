using System.Text;

namespace ComfyTunnel;

public sealed record ParsedCommand(string Host, int Port, string User, string? KeyPath);

public static class SshCommandParser
{
    public static ParsedCommand Parse(string input)
    {
        if (input.Length > 4096 || input.IndexOfAny(['\r', '\n', '|', '&', ';', '`', '$', '<', '>']) >= 0)
            throw new FormatException("仅接受单条 SSH 连接命令，不支持命令组合或 shell 表达式。");
        var words = Tokenize(input);
        if (words.Count == 0 || !(words[0].Equals("ssh", StringComparison.OrdinalIgnoreCase) || words[0].Equals("ssh.exe", StringComparison.OrdinalIgnoreCase)))
            throw new FormatException("命令应以 ssh 开头，例如 ssh -p 12345 ubuntu@gpu.example.com。");
        string? destination = null, user = null, key = null;
        int port = 22;
        var seen = new HashSet<char>();
        for (int i = 1; i < words.Count; i++)
        {
            string w = words[i];
            if (w.StartsWith('-'))
            {
                if (w.Length < 2 || "pli".IndexOf(w[1]) < 0) throw new FormatException("暂只支持 -p、-l、-i 参数，请移除其他选项。");
                char option = w[1];
                if (!seen.Add(option)) throw new FormatException("同一个 SSH 参数不能重复填写。");
                string value = w.Length > 2 ? w[2..] : ++i < words.Count ? words[i] : "";
                if (string.IsNullOrWhiteSpace(value) || value.StartsWith('-')) throw new FormatException("SSH 参数缺少值。");
                switch (option)
                {
                    case 'p':
                        if (!int.TryParse(value, out port) || port is < 1 or > 65535) throw new FormatException("SSH 端口必须在 1–65535 之间。");
                        break;
                    case 'l': user = value; break;
                    case 'i': key = value; break;
                }
            }
            else
            {
                if (destination != null) throw new FormatException("只允许一个服务器地址，不支持远端命令。");
                destination = w;
            }
        }
        if (destination == null) throw new FormatException("缺少服务器地址。");
        int at = destination.IndexOf('@');
        if (at >= 0)
        {
            if (user != null || at == 0 || destination.LastIndexOf('@') != at) throw new FormatException("用户名重复或格式错误。");
            user = destination[..at]; destination = destination[(at + 1)..];
        }
        if (destination.StartsWith('[') && destination.EndsWith(']')) destination = destination[1..^1];
        else if (destination.Contains(':')) throw new FormatException("IPv6 请使用方括号包围；SSH 端口通过 -p 指定。");
        if (!Profile.ValidHost(destination)) throw new FormatException("服务器地址格式错误。");
        if (user != null && (user.Any(char.IsWhiteSpace) || user.Any(char.IsControl))) throw new FormatException("用户名格式错误。");
        return new(destination, port, user ?? "", key);
    }

    private static List<string> Tokenize(string input)
    {
        var result = new List<string>(); var word = new StringBuilder(); char quote = '\0'; bool started = false;
        foreach (char c in input.Trim())
        {
            if (quote != '\0') { if (c == quote) quote = '\0'; else word.Append(c); started = true; }
            else if (c is '\'' or '"') { quote = c; started = true; }
            else if (char.IsWhiteSpace(c)) { if (started) { result.Add(word.ToString()); word.Clear(); started = false; } }
            else { word.Append(c); started = true; }
        }
        if (quote != '\0') throw new FormatException("命令中的引号没有闭合。");
        if (started) result.Add(word.ToString());
        return result;
    }
}
