# Build and test

Requirements: .NET 8 SDK and the Windows App SDK; the sibling folder `MynaUI` must exist.

```
dotnet build BBSDesktop/BBSApp/BBSApp.csproj -c Release -p:Platform=x64 -p:WindowsPackageType=None
dotnet test BBSDesktop/AQC.CadImport.Tests
dotnet test BBSDesktop/Office.Shared.Tests
```

The combined installer (`MynaOpsCAD-Setup`) is built from the MynaHub repository with `installer/suite/build-all.ps1`.
