namespace InputFinder;

static class LogPaths
{
    public static string Directory
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "InputFinder.csproj")))
                    return Path.Combine(dir.FullName, "logs");
                dir = dir.Parent;
            }

            return Path.Combine(AppContext.BaseDirectory, "logs");
        }
    }
}
