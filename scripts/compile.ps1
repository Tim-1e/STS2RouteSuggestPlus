param(
    [Parameter(Mandatory)][string[]]$Source,
    [Parameter(Mandatory)][string]$Output,
    [string]$GameDir = 'C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2',
    [string[]]$ExtraReferences = @(),
    [switch]$Executable
)
$ErrorActionPreference = 'Stop'
[void][Reflection.Assembly]::LoadFrom((Join-Path $PSHOME 'Microsoft.CodeAnalysis.dll'))
[void][Reflection.Assembly]::LoadFrom((Join-Path $PSHOME 'Microsoft.CodeAnalysis.CSharp.dll'))
$taskRefs = [Collections.Generic.List[Microsoft.CodeAnalysis.MetadataReference]]::new()
foreach ($taskFile in @((Get-ChildItem -LiteralPath (Join-Path $GameDir 'data_sts2_windows_x86_64') -Filter '*.dll').FullName) + $ExtraReferences) {
    try {
        [void][Reflection.AssemblyName]::GetAssemblyName($taskFile)
        $taskRefs.Add([Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($taskFile))
    } catch [BadImageFormatException] { }
}
$taskKind = if ($Executable) { [Microsoft.CodeAnalysis.OutputKind]::ConsoleApplication } else { [Microsoft.CodeAnalysis.OutputKind]::DynamicallyLinkedLibrary }
$taskOptions = [Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new($taskKind).WithOptimizationLevel([Microsoft.CodeAnalysis.OptimizationLevel]::Release).WithNullableContextOptions([Microsoft.CodeAnalysis.NullableContextOptions]::Enable).WithDeterministic($true)
$taskTrees = foreach ($taskFile in $Source) {
    [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText((Resolve-Path -LiteralPath $taskFile)), $null, $taskFile)
}
$taskCompilation = [Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create([IO.Path]::GetFileNameWithoutExtension($Output), [Microsoft.CodeAnalysis.SyntaxTree[]]@($taskTrees), [Microsoft.CodeAnalysis.MetadataReference[]]$taskRefs, $taskOptions)
[void](New-Item -ItemType Directory -Path (Split-Path -Parent $Output) -Force)
$taskStream = [IO.File]::Create($Output + '.tmp')
try { $taskResult = $taskCompilation.Emit($taskStream) } finally { $taskStream.Dispose() }
$taskResult.Diagnostics | Where-Object Severity -ne Hidden | ForEach-Object ToString
if (-not $taskResult.Success) { throw 'Compilation failed' }
Move-Item -LiteralPath ($Output + '.tmp') -Destination $Output -Force
if ($Executable) {
    '{"runtimeOptions":{"tfm":"net9.0","framework":{"name":"Microsoft.NETCore.App","version":"9.0.7"}}}' | Set-Content -LiteralPath ([IO.Path]::ChangeExtension($Output, '.runtimeconfig.json')) -Encoding utf8
}
Write-Output "COMPILE_PASS $Output"
