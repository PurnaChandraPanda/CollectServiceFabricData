param(
    $project = "$psscriptroot\..\CollectSFData\CollectSFData.csproj",
    [ValidateSet('Debug','Release')]
    $configuration = 'Debug',
    $framework = 'net5',
    $runtime = 'win-x64'
)

dotnet restore $project

dotnet build $project -c $configuration --framework $framework

dotnet publish $project -r $runtime -c $configuration --self-contained $true --no-dependencies -p:PublishSingleFile=true -p:PublishedTrimmed=true --framework $framework

