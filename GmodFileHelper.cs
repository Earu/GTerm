using System.Text;

namespace GTerm
{
    internal static class GmodFileHelper
    {
        public static string GenerateDirectoryTree(string path, int maxDepth = 5)
        {
            if (!Directory.Exists(path))
            {
                return $"Directory not found: {path}";
            }

            StringBuilder sb = new();
            sb.AppendLine(path);
            GenerateTreeRecursive(path, "", true, sb, 0, maxDepth);
            return sb.ToString();
        }

        private static void GenerateTreeRecursive(string path, string indent, bool isLast, StringBuilder sb, int currentDepth, int maxDepth)
        {
            if (currentDepth >= maxDepth)
            {
                sb.AppendLine($"{indent}{(isLast ? "└── " : "├── ")}[max depth reached]");
                return;
            }

            try
            {
                string[] directories = Directory.GetDirectories(path);
                string[] files = Directory.GetFiles(path);

                // Combine directories and files
                var items = directories.Select(d => (Path: d, IsDirectory: true))
                    .Concat(files.Select(f => (Path: f, IsDirectory: false)))
                    .OrderBy(x => !x.IsDirectory)  // Directories first
                    .ThenBy(x => Path.GetFileName(x.Path))
                    .ToArray();

                for (int i = 0; i < items.Length; i++)
                {
                    bool isLastItem = (i == items.Length - 1);
                    string name = Path.GetFileName(items[i].Path);
                    string marker = isLastItem ? "└── " : "├── ";
                    string newIndent = indent + (isLast ? "    " : "│   ");

                    if (items[i].IsDirectory)
                    {
                        sb.AppendLine($"{indent}{marker}{name}/");
                        GenerateTreeRecursive(items[i].Path, newIndent, isLastItem, sb, currentDepth + 1, maxDepth);
                    }
                    else
                    {
                        FileInfo fi = new(items[i].Path);
                        string size = FormatFileSize(fi.Length);
                        sb.AppendLine($"{indent}{marker}{name} ({size})");
                    }
                }
            }
            catch (UnauthorizedAccessException)
            {
                sb.AppendLine($"{indent}{(isLast ? "└── " : "├── ")}[Access Denied]");
            }
            catch (Exception ex)
            {
                sb.AppendLine($"{indent}{(isLast ? "└── " : "├── ")}[Error: {ex.Message}]");
            }
        }

        private static string FormatFileSize(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB" };
            double len = bytes;
            int order = 0;
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len /= 1024;
            }
            return $"{len:0.##} {sizes[order]}";
        }

        public static string ReadFile(string basePath, string relativePath, int maxSizeKB = 1024)
        {
            try
            {
                // Normalize the path and prevent directory traversal attacks
                string fullPath = Path.GetFullPath(Path.Combine(basePath, relativePath));
                string normalizedBasePath = Path.GetFullPath(basePath);

                // Ensure the requested file is within the Garry's Mod directory
                if (!fullPath.StartsWith(normalizedBasePath, StringComparison.OrdinalIgnoreCase))
                {
                    return "Error: Access denied - path is outside Garry's Mod directory";
                }

                if (!File.Exists(fullPath))
                {
                    return $"Error: File not found: {relativePath}";
                }

                FileInfo fi = new(fullPath);
                long maxSizeBytes = maxSizeKB * 1024;

                if (fi.Length > maxSizeBytes)
                {
                    return $"Error: File too large ({FormatFileSize(fi.Length)}). Maximum size: {maxSizeKB} KB";
                }

                // Try to read as text
                try
                {
                    string content = File.ReadAllText(fullPath);
                    return content;
                }
                catch (Exception)
                {
                    // If text reading fails, it might be binary
                    return "Error: File appears to be binary or cannot be read as text";
                }
            }
            catch (Exception ex)
            {
                return $"Error: {ex.Message}";
            }
        }
    }
}

