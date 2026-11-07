@{
    RootModule            = 'Dav.dll'
    ModuleVersion         = '3.0.1'
    GUID                  = '5d1c7e7a-3b0e-4f0e-9a6c-2f4e8b1d7c35'
    Author                = 'Aizaz Zaheen'
    CompanyName           = 'Aizaz Zaheen'
    Copyright             = '(c) Aizaz Zaheen. MIT License.'
    Description           = 'Checks a .NET assembly without running it: risky API calls, known vulnerabilities in its dependencies, API changes between two versions, and circular dependencies. Risky APIs, vulnerabilities and a too-small version bump fail the command; circular dependencies only warn.'
    PowerShellVersion     = '5.1'
    CompatiblePSEditions  = @('Desktop', 'Core')
    CmdletsToExport       = @('Get-DavRiskyApi', 'Get-DavVulnerability', 'Compare-DavVersion', 'Get-DavCircularDependency', 'Invoke-DavCheck')
    FunctionsToExport     = @()
    AliasesToExport       = @()
    VariablesToExport     = @()
    PrivateData           = @{
        PSData = @{
            Tags         = @('dotnet', 'assembly', 'security', 'vulnerability', 'ci', 'PSEdition_Desktop', 'PSEdition_Core', 'Windows', 'Linux', 'MacOS')
            LicenseUri   = 'https://github.com/Aizaz-hash/dotnet-Assembly-Visualizer/blob/v3/LICENSE'
            ProjectUri   = 'https://github.com/Aizaz-hash/dotnet-Assembly-Visualizer'
            ReleaseNotes = 'First release of the Dav module.'
        }
    }
}
