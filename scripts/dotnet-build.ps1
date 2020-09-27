param(
    $project = "$psscriptroot\..\CollectSFData\CollectSFData.csproj",
    [ValidateSet('Debug', 'Release')]
    [string[]]$configuration = @('Debug', 'Release'),
    $framework = 'net5',
    $runtime = 'win-x64'
)

dotnet restore $project

foreach ($config in @($configuration)) {
    dotnet build $project -c $config --framework $framework
}

# kusto doesnt like being in singlefile
#dotnet publish $project -r $runtime -c $configuration --self-contained $true --no-dependencies -p:PublishSingleFile=true -p:PublishedTrimmed=true --framework $framework

