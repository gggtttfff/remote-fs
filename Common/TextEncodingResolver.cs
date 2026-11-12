using System.Text;

namespace RemoteFs.Common;

/// <summary>
/// 解码结果：文本 + 实际使用的编码 + BOM 信息 + 失败原因
/// </summary>
public sealed record DecodeOutcome
{
    /// <summary>是否解码成功</summary>
    public bool Success { get; init; }

    /// <summary>解码后的文本；失败时为 null</summary>
    public string? Text { get; init; }

    /// <summary>实际使用的编码名</summary>
    public string EncodingName { get; init; } = "utf-8";

    /// <summary>原内容是否带 BOM</summary>
    public bool HasBom { get; init; }

    /// <summary>BOM 字节数；没有 BOM 时为 0</summary>
    public int PreambleLength { get; init; }

    /// <summary>是否判定为二进制内容（含 NUL 字节）</summary>
    public bool IsBinary { get; init; }

    /// <summary>失败原因，可直接展示给调用方</summary>
    public string? Error { get; init; }
}

/// <summary>
/// 识别到的字节序标记
/// </summary>
/// <param name="EncodingName">BOM 对应的编码名；没有 BOM 时为 null</param>
/// <param name="PreambleLength">BOM 字节数；没有 BOM 时为 0</param>
public readonly record struct DetectedBom(string? EncodingName, int PreambleLength)
{
    /// <summary>是否检测到 BOM</summary>
    public bool Found => EncodingName is not null;
}

/// <summary>
/// 文本编码解析器：把编码名解析成具体编码，并负责 BOM 识别与文本解码。
/// 只处理字节数组，不依赖任何传输层，便于 MCP 主程序与备用通道共用。
/// </summary>
public static class TextEncodingResolver
{
    /// <summary>自动模式：有 BOM 按 BOM 解码，没有 BOM 按严格 UTF-8 解码</summary>
    public const string Auto = "auto";

    /// <summary>不带 BOM 的 UTF-8。写文件与任务脚本统一用它</summary>
    public static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

    /// <summary>宽松 UTF-8，解码失败时用替换字符而不是抛异常</summary>
    public static readonly Encoding LenientUtf8 = new UTF8Encoding(false, false);

    private static readonly object SyncRoot = new();
    private static bool _providerRegistered;

    /// <summary>
    /// 编码名到代码页的映射。
    /// 走数字代码页而不是字符串名：CodePages 提供程序对字符串名的解析并不完整。
    /// </summary>
    private static readonly Dictionary<string, int> CodePages = new(StringComparer.OrdinalIgnoreCase)
    {
        ["gbk"] = 936,
        ["gb2312"] = 936,
        ["gb18030"] = 54936,
        ["big5"] = 950,
        ["shift_jis"] = 932,
        ["sjis"] = 932,
        ["euc-jp"] = 20932,
        ["euc-kr"] = 51949,
        ["koi8-r"] = 20866,
        ["windows-1252"] = 1252
    };

    /// <summary>所有可选编码名，供界面下拉框与错误提示共用</summary>
    public static IReadOnlyList<string> SupportedNames { get; } = new[]
    {
        Auto, "utf-8", "utf-8-bom", "utf-16le", "utf-16be", "utf-32le", "utf-32be",
        "gbk", "gb2312", "gb18030", "big5", "shift_jis", "euc-jp", "euc-kr", "latin1"
    };

    /// <summary>
    /// 注册 CodePages 编码提供程序，使 gb18030/gbk/big5/shift_jis 等代码页可用。
    /// 重复调用无副作用；失败只记录告警，不影响 UTF-8 路径。
    /// </summary>
    public static void EnsureProviderRegistered()
    {
        lock (SyncRoot)
        {
            if (_providerRegistered)
            {
                return;
            }
            _providerRegistered = true;
            try
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            }
            catch (Exception exception)
            {
                AuditLogger.Warn(
                    $"注册 CodePages 编码提供程序失败，GBK/GB18030 等编码将不可用: {exception.Message}");
            }
        }
    }

    /// <summary>规范化编码名：去空白、小写、下划线转连字符</summary>
    public static string Normalize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Auto;
        }
        return name.Trim().ToLowerInvariant().Replace('_', '-');
    }

    /// <summary>编码名是否受支持</summary>
    public static bool IsSupported(string? name)
    {
        var normalized = Normalize(name);
        return SupportedNames.Contains(normalized)
            || CodePages.ContainsKey(normalized)
            || normalized is "utf8" or "utf-16" or "iso-8859-1";
    }

    /// <summary>
    /// 解析用于写入的编码；auto 视为 utf-8。
    /// 使用异常回退，写入无法表示的字符会抛异常而不是静默变成问号。
    /// </summary>
    public static Encoding ResolveForWrite(string? name)
    {
        var normalized = Normalize(name);
        if (normalized == Auto)
        {
            normalized = "utf-8";
        }
        return ResolvePair(normalized)?.Strict ?? Utf8NoBom;
    }

    /// <summary>
    /// 按原文件的编码与 BOM 情况选择写回用的编码。
    /// UTF-8 要带 BOM 时必须切到 utf-8-bom，否则普通 UTF-8 编码的 preamble 是空的，BOM 写不出来
    /// </summary>
    public static (Encoding Encoding, bool WriteBom) ResolveForRewrite(string? name, bool hasBom)
    {
        var normalized = Normalize(name);
        if (hasBom && (normalized == "utf-8" || normalized == "utf8" || normalized == Auto))
        {
            return (ResolveForWrite("utf-8-bom"), true);
        }
        return (ResolveForWrite(normalized), hasBom);
    }

    /// <summary>
    /// 解析用于流式读取的宽松编码；auto 或未知名返回 null，调用方自行回退到 UTF-8。
    /// </summary>
    public static Encoding? ResolveLenient(string? name)
    {
        var normalized = Normalize(name);
        if (normalized == Auto)
        {
            return null;
        }
        return ResolvePair(normalized)?.Lenient;
    }

    /// <summary>
    /// 从字节头识别 BOM。
    /// 顺序很重要：UTF-32LE 的 FF FE 00 00 必须先于 UTF-16LE 的 FF FE 判断，
    /// 否则 UTF-32 文件会被当成 UTF-16，后两个字节变成 U+0000。
    /// </summary>
    public static DetectedBom DetectBom(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 4 && data[0] == 0xFF && data[1] == 0xFE && data[2] == 0x00 && data[3] == 0x00)
        {
            return new DetectedBom("utf-32le", 4);
        }
        if (data.Length >= 4 && data[0] == 0x00 && data[1] == 0x00 && data[2] == 0xFE && data[3] == 0xFF)
        {
            return new DetectedBom("utf-32be", 4);
        }
        if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
        {
            return new DetectedBom("utf-8", 3);
        }
        if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE)
        {
            return new DetectedBom("utf-16le", 2);
        }
        if (data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF)
        {
            return new DetectedBom("utf-16be", 2);
        }
        return default;
    }

    /// <summary>
    /// 按指定编码解码字节。
    /// encodingName 为 null 或 auto 时：先看 BOM，没有 BOM 则按 UTF-8 严格解码。
    /// </summary>
    /// <param name="content">待解码内容</param>
    /// <param name="encodingName">编码名；null 或 auto 表示有 BOM 按 BOM、没有按 UTF-8</param>
    /// <param name="truncated">内容是否按字节截断。截断时改用宽松解码，避免切在多字节字符中间被误判</param>
    public static DecodeOutcome Decode(byte[] content, string? encodingName, bool truncated)
    {
        var name = Normalize(encodingName);

        // 1. BOM 是自描述元数据，优先于任何配置
        var bom = DetectBom(content);
        if (bom.Found && ResolvePair(bom.EncodingName!) is { } bomPair)
        {
            var body = content[bom.PreambleLength..];
            if (!TryStrictDecode(body, bomPair.Strict, out var bomText, out _))
            {
                // BOM 说明了编码却解不出来，多半是文件被截断在半路
                bomText = bomPair.Lenient.GetString(body).TrimEnd('�');
            }
            return new DecodeOutcome
            {
                Success = true,
                Text = bomText,
                EncodingName = bom.EncodingName!,
                HasBom = true,
                PreambleLength = bom.PreambleLength
            };
        }

        // 2. 显式指定了具体编码：严格按它解码
        if (name != Auto && name != "utf-8" && name != "utf8")
        {
            var pair = ResolvePair(name);
            if (pair is null)
            {
                return new DecodeOutcome { Success = false, Error = $"不支持的编码 '{encodingName}'" };
            }

            // UTF-16/32 的文本必然含 NUL，不能按二进制判定
            if (!IsWideEncoding(pair.Strict) && ContainsNul(content))
            {
                return new DecodeOutcome { Success = false, IsBinary = true, EncodingName = name };
            }
            return DecodeWith(pair, content, name, truncated);
        }

        // 3. auto / utf-8：无 BOM 时按 UTF-8 严格解码
        if (ContainsNul(content))
        {
            return new DecodeOutcome { Success = false, IsBinary = true, EncodingName = "utf-8" };
        }
        return DecodeWith(ResolvePair("utf-8")!, content, "utf-8", truncated);
    }

    /// <summary>
    /// 把文本编码成字节。UTF-8 默认不带 BOM，UTF-16/UTF-32 始终带 BOM（否则无法自描述）。
    /// </summary>
    public static byte[] EncodeBytes(string content, Encoding encoding, bool writeBom)
    {
        var body = encoding.GetBytes(content);
        var preamble = encoding.GetPreamble();

        // UTF-16/32 不带 BOM 就没有自描述信息，强制补上
        var needBom = writeBom || (preamble.Length > 0 && IsWideEncoding(encoding));
        if (!needBom || preamble.Length == 0)
        {
            return body;
        }

        var payload = new byte[preamble.Length + body.Length];
        preamble.CopyTo(payload, 0);
        body.CopyTo(payload, preamble.Length);
        return payload;
    }

    /// <summary>
    /// 解码并附带失败原因
    /// </summary>
    private static DecodeOutcome DecodeWith(EncodingPair pair, byte[] content, string name, bool truncated)
    {
        if (TryStrictDecode(content, pair.Strict, out var text, out var error))
        {
            return new DecodeOutcome { Success = true, Text = text, EncodingName = name };
        }

        // 截断时用宽松解码兜底，避免切在多字节字符中间被误判
        if (truncated)
        {
            var lenient = pair.Lenient.GetString(content).TrimEnd('�');
            return new DecodeOutcome { Success = true, Text = lenient, EncodingName = name };
        }

        return new DecodeOutcome { Success = false, EncodingName = name, Error = error };
    }

    /// <summary>
    /// 严格解码，失败时返回可读的失败原因
    /// </summary>
    private static bool TryStrictDecode(
        byte[] content, Encoding encoding, out string text, out string? error)
    {
        try
        {
            text = encoding.GetString(content);
            error = null;
            return true;
        }
        catch (DecoderFallbackException exception)
        {
            text = string.Empty;
            error = exception.Message;
            return false;
        }
    }

    /// <summary>
    /// 是否含有 NUL 字节。含 NUL 通常意味着是二进制内容
    /// </summary>
    private static bool ContainsNul(byte[] content)
    {
        foreach (var value in content)
        {
            if (value == 0)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 是否是宽字符编码。它们的文本必然含 NUL 字节，不能用 NUL 判定二进制
    /// </summary>
    public static bool IsWideEncoding(Encoding encoding)
    {
        return encoding.CodePage is 1200 or 1201 or 12000 or 12001;
    }

    /// <summary>
    /// 解析编码名对应的编码实例对；auto 或未知名返回 null
    /// </summary>
    private static EncodingPair? ResolvePair(string normalized)
    {
        return normalized switch
        {
            "utf-8" or "utf8" => new EncodingPair(
                new UTF8Encoding(false, true), new UTF8Encoding(false, false)),
            "utf-8-bom" => new EncodingPair(
                new UTF8Encoding(true, true), new UTF8Encoding(true, false)),
            // UnicodeEncoding/UTF32Encoding 没有带 fallback 的构造函数，
            // 严格实例走 GetEncoding 的代码页重载
            "utf-16le" or "utf-16" => new EncodingPair(
                StrictEncoding(1200), new UnicodeEncoding(false, false)),
            "utf-16be" => new EncodingPair(
                StrictEncoding(1201), new UnicodeEncoding(true, false)),
            "utf-32le" => new EncodingPair(
                StrictEncoding(12000), new UTF32Encoding(false, false)),
            "utf-32be" => new EncodingPair(
                StrictEncoding(12001), new UTF32Encoding(true, false)),
            "latin1" or "iso-8859-1" => new EncodingPair(
                StrictEncoding(28591), Encoding.Latin1),
            _ => ResolveCodePage(normalized)
        };
    }

    /// <summary>
    /// 取严格编码实例：遇到无法表示的字符抛异常，而不是静默变成问号
    /// </summary>
    private static Encoding StrictEncoding(int codePage)
    {
        return Encoding.GetEncoding(
            codePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    }

    /// <summary>
    /// 解析代码页编码。平台不支持时返回 null，由调用方给出可读的错误
    /// </summary>
    private static EncodingPair? ResolveCodePage(string normalized)
    {
        if (!CodePages.TryGetValue(normalized, out var codepage))
        {
            return null;
        }

        try
        {
            var strict = Encoding.GetEncoding(
                codepage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            var lenient = Encoding.GetEncoding(codepage);
            return new EncodingPair(strict, lenient);
        }
        catch (Exception)
        {
            // 该代码页在当前平台不可用
            return null;
        }
    }

    /// <summary>
    /// 一个编码名的严格与宽松两个实例
    /// </summary>
    private sealed record EncodingPair(Encoding Strict, Encoding Lenient);
}
