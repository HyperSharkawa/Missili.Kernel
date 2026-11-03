using System.Text.Json;
using Missili.Abstractions;

namespace Missili.Adapters.OneBot;

/// <summary>平台媒体描述解析，远端路径只有显式映射时才作为本机文件访问。</summary>
internal sealed partial class OneBotAdapter
{
    /// <summary>优先读取已映射原始文件，再请求标准图片或转换语音，不猜测视频查询接口。</summary>
    public async Task<MediaSourceRead> ResolveMediaAsync(MediaSource source, CancellationToken cancellationToken)
    {
        if (source.Kind != "onebot11.media" || source.Version != 1) throw new NotSupportedException("媒体来源格式不支持。");
        string file = OneBotCodec.String(source.Data, "file"), type = OneBotCodec.String(source.Data, "type");
        if (Uri.TryCreate(file, UriKind.Absolute, out Uri? uri) && uri.Scheme is "http" or "https") return new MediaSourceRead { Redirect = MediaSource.Http(uri) };
        string? mapped = MapFile(file);
        if (mapped is not null) return OpenFile(mapped, null);
        if (type == "video") throw new MediaAcquisitionException("标准没有视频文件查询接口，且未提供可读取来源。");
        Dictionary<string, object> parameters = new() { ["file"] = file };
        if (type == "record") parameters["out_format"] = _options.RecordFormat;
        else if (type != "image") throw new NotSupportedException("不支持的媒体类别。");
        PlatformOperationResult result = await InvokeAsync(type == "image" ? "get_image" : "get_record", JsonSerializer.SerializeToElement(parameters), cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (result.State == PlatformOperationState.Unavailable || (result.State == PlatformOperationState.Unknown && !GetStatus().ApiAvailable)) throw new AdapterUnavailableException();
        if (result.State != PlatformOperationState.Succeeded || result.Data is null) throw new InvalidDataException("平台媒体查询未成功。");
        JsonElement? representation = type == "record" ? JsonSerializer.SerializeToElement(new Dictionary<string, object>
        { ["format"] = _options.RecordFormat, ["original"] = false, ["source"] = "onebot11.get_record" }) : null;
        JsonElement data = result.Data.Value;
        if (type == "record" && _options.CompatibilityProfile == CompatibilityProfile.SnowLuma && data.TryGetProperty("base64", out JsonElement base64))
        {
            string text = base64.GetString() ?? throw new InvalidDataException("Base64 扩展为空。");
            return new MediaSourceRead { Content = new MemoryStream(Convert.FromBase64String(text), writable: false), Representation = representation };
        }
        string path = OneBotCodec.String(data, "file");
        mapped = MapFile(path);
        if (mapped is null) throw new MediaAcquisitionException("对端只返回文件路径，但未配置可访问映射。");
        return OpenFile(mapped, representation);
    }

    /// <summary>取得独立只读流，解析调用资格由宿主保留到此流被释放。</summary>
    private static MediaSourceRead OpenFile(string path, JsonElement? representation) => new()
    { Content = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan), Representation = representation };

    /// <summary>检查远端前缀与每一级实际链接目标，符号链接不能逃逸配置的本地目录。</summary>
    private string? MapFile(string remote)
    {
        string value = remote.Replace('\\', '/');
        if (Uri.TryCreate(remote, UriKind.Absolute, out Uri? uri) && uri.IsFile) value = uri.LocalPath.Replace('\\', '/');
        foreach (MediaPathMapping mapping in _options.PathMappings)
        {
            string prefix = mapping.RemoteRoot.Replace('\\', '/').TrimEnd('/') + "/";
            StringComparison remoteComparison = prefix.Contains(':') ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!value.StartsWith(prefix, remoteComparison)) continue;
            string relative = value[prefix.Length..];
            string[] parts = relative.Split('/');
            if (parts.Any(part => part is "" or "." or ".." || part.Contains(':'))) throw new InvalidDataException("媒体映射相对路径无效。");
            DirectoryInfo root = new(Path.GetFullPath(mapping.LocalRoot));
            string allowed = root.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? root.FullName;
            string current = allowed;
            StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            for (int index = 0; index < parts.Length; index++)
            {
                string candidate = Path.GetFullPath(Path.Combine(current, parts[index]));
                FileSystemInfo info = index == parts.Length - 1 ? new FileInfo(candidate) : new DirectoryInfo(candidate);
                current = info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? info.FullName;
                if (!current.StartsWith(allowed.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, comparison))
                    throw new InvalidDataException("媒体映射或符号链接超出允许目录。");
            }
            return current;
        }
        return null;
    }
}
