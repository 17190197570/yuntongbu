# Shared allowlist for review and source packaging. This file performs no action when loaded.
function Get-PublicSourceFiles {
    param([Parameter(Mandatory)][string]$ProjectRoot)
    $publicRootFiles = @('.gitignore', '.gitattributes', 'LICENSE', 'README.md',
        'THIRD_PARTY_NOTICES.md', 'CONTRIBUTING.md', 'PRIVACY.md', 'SECURITY.md',
        'CMakeLists.txt', 'Directory.Build.props', 'global.json')
    foreach ($publicName in $publicRootFiles) {
        $publicPath = Join-Path $ProjectRoot $publicName
        if (Test-Path -LiteralPath $publicPath -PathType Leaf) {
            $publicItem = Get-Item -LiteralPath $publicPath -Force
            if ($publicItem.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Source link rejected: $publicName" }
            $publicItem
        }
    }
    $publicDirectories = [Collections.Generic.Stack[string]]::new()
    foreach ($publicDirectory in @('src', 'tests', 'scripts', 'docs', 'installer', '.github')) {
        $publicPath = Join-Path $ProjectRoot $publicDirectory
        if (Test-Path -LiteralPath $publicPath -PathType Container) { $publicDirectories.Push($publicPath) }
    }
    while ($publicDirectories.Count) {
        $publicDirectory = $publicDirectories.Pop()
        if ((Get-Item -LiteralPath $publicDirectory -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "Source directory link rejected: $([IO.Path]::GetRelativePath($ProjectRoot, $publicDirectory))"
        }
        foreach ($publicItem in (Get-ChildItem -LiteralPath $publicDirectory -Force)) {
            if ($publicItem.PSIsContainer -and $publicItem.Name -in @('bin', 'obj', '__pycache__', 'node_modules', '.git', '.vs', '.private', '.local')) { continue }
            $publicRelative = [IO.Path]::GetRelativePath($ProjectRoot, $publicItem.FullName).Replace('\', '/')
            if ($publicItem.Name -in @('update-trust.json', 'secrets.json', '.env') -or
                $publicItem.Name.StartsWith('.env.', [StringComparison]::OrdinalIgnoreCase) -or
                $publicItem.Extension -in @('.db', '.db-wal', '.db-shm', '.sqlite', '.sqlite3', '.log', '.pfx', '.p12', '.key', '.pem', '.pyc')) { continue }
            if ($publicItem.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Source link rejected: $publicRelative" }
            if ($publicItem.PSIsContainer) { $publicDirectories.Push($publicItem.FullName); continue }
            if ($publicItem.Name -eq 'CMakeLists.txt' -or $publicItem.Extension -in @('.cs', '.csproj', '.cpp', '.h', '.rc', '.manifest', '.sql', '.ps1', '.py', '.md', '.json', '.wxs', '.yml', '.yaml')) {
                $publicItem
            } else { throw "Unreviewed source file type: $publicRelative" }
        }
    }
}
