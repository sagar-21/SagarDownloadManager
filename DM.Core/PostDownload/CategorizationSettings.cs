namespace DM.Core.PostDownload;

public sealed class CategorizationSettings
{
    public bool Enabled { get; set; } = true;

    public List<CategorizationRule> Rules { get; set; } =
    [
        new() { CategoryName = "Videos",    TargetFolder = "Videos",    Extensions = ["mp4","mkv","avi","mov","wmv","flv","webm","m4v"] },
        new() { CategoryName = "Music",     TargetFolder = "Music",     Extensions = ["mp3","flac","aac","wav","ogg","m4a","wma","opus"] },
        new() { CategoryName = "Documents", TargetFolder = "Documents", Extensions = ["pdf","doc","docx","xls","xlsx","ppt","pptx","txt","epub","mobi"] },
        new() { CategoryName = "Software",  TargetFolder = "Software",  Extensions = ["exe","msi","dmg","pkg","deb","rpm","zip","7z","tar","gz","rar","iso"] },
    ];
}

public sealed class CategorizationRule
{
    public string       CategoryName { get; set; } = "";
    /// <summary>Relative to the download directory, or absolute path.</summary>
    public string       TargetFolder { get; set; } = "";
    /// <summary>Lowercase extensions without leading dot (e.g. "mp4", "mkv").</summary>
    public List<string> Extensions   { get; set; } = [];
    public bool         Enabled      { get; set; } = true;
}
