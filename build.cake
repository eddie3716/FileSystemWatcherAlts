#tool "NUnit.ConsoleRunner"
#tool "NUnit.Extension.NUnitV2ResultWriter"
#tool "nuget:?package=vswhere"
#tool "nuget:?package=GitVersion.CommandLine"
#tool "nuget:?package=Newtonsoft.Json"
#addin "Cake.Powershell"
#load "./build-ext.cake"

var configuration = Argument("configuration", "Debug");
var target = Argument("target", "Build");
var buildNumber = Argument("buildNumber", "0");
var isMergeBuild = bool.Parse(Argument("merge", "false"));
var pkgRepoApiKey = Argument("pkgRepoApiKey", "");
var publishPackages = bool.Parse(Argument("publishPackages", "false"));

var srcRoot =  new DirectoryPath("./");
var nupkgPublishPath = System.IO.Path.GetFullPath("./nupkgs");
var slnFiles = System.IO.Directory.GetFiles(@"./", "FileSystemWatcherAlts.sln");

var gitVersion = GitVersion(new GitVersionSettings{
    UpdateAssemblyInfo = false,
});

if (slnFiles.Length == 0)
{
	throw new Exception("No solution files. There's nothing to build.");
}

var companyName = "Billtrust";

string majorMinorVersion;
string versionSuffix;

if(Jenkins.IsRunningOnJenkins)
{
    var env = Jenkins.Environment;
    buildNumber = env.Build.BuildNumber.ToString();
	Information("Running on Jenkins. Using the auto-incrementing Buildnumber provided by Jenkins. '{0}'", buildNumber);
	var versioning = GetFromLocalVersionFile();
	majorMinorVersion =  versioning.Item1;
	if (!String.IsNullOrEmpty(versioning.Item2))
	{
		versionSuffix = "-" + versioning.Item2;
	}
}
else
{
	Information("Not running on Jenkins. BuildNumber will remain set to '{0}'", buildNumber);
	
	majorMinorVersion = "0.0";
	Information("Not running on Jenkins. Forcing the Major.Minor version to be '0.0'");
	
	versionSuffix = "-sha." + gitVersion.Sha.Substring(0,8);
	Information("Not running on Jenkins. Forcing the Major.Minor version to be '0.0'");
}

var version = string.Format("{0}.{1}.{2}", majorMinorVersion, buildNumber, gitVersion.FullBuildMetaData);
var numericVersion = string.Format("{0}.{1}", majorMinorVersion, buildNumber);
var pkgVersion = string.Format("{0}.{1}{2}", majorMinorVersion, buildNumber, versionSuffix);

const string BPS = "BT.Vuebill.bps";
const string BPS_DATA = "BT.Vuebill.bps.Data";
const string WEAK_EVENT_MESSAGE_BROKER = "BT.Vuebill.WeakEventMessageBroker";
const string WEAK_EVENT_MESSAGE_BROKER_UNITY = "BT.Vuebill.WeakEventMessageBroker.Unity";
const string CONNECTION_STRINGS = "BT.Vuebill.bpsConnectionStrings";
const string BPSLOGIC2 = "BT.Vuebill.BPSLogic2";
const string SERVICE_LOCATION = "BT.Vuebill.bpsServiceLocation";
const string AUDIT_FILEFOWARDER = "BT.Vuebill.AuditFileForwarding";
const string PCI_COMPLIANCE = "BT.Vuebill.PciCompliance";


Information("Version: {0}", version);
Information("Numeric Version: {0}", numericVersion);
Information("Package Version: {0}", pkgVersion);

Task("RealClean")
	.Does(() =>
	{
		try
		{
			CleanDirectories("./**/bin");
			CleanDirectories("./**/obj");
			CleanDirectory("./nupkgs");
			CleanDirectory("./packages");
		}
		catch (Exception ex)
		{
			Information("Error in RealClean: {0} {1}", ex.Message, ex.StackTrace);
        }
	});

Task("Version")
    .Does(() =>
    {
		var assemblyInfos = GetFiles("./**/AssemblyInfo.cs");

			foreach(var file in assemblyInfos)
			{
				Information("Setting Version Info on {0}...", file);

				var parsedAssemblyInfo = ParseAssemblyInfo(file);

				CreateAssemblyInfo(
					file,
					new AssemblyInfoSettings
					{
					Title = parsedAssemblyInfo.Title,
					Product = parsedAssemblyInfo.Product,
					Description = parsedAssemblyInfo.Description,
					ComVisible = false,
					Version = numericVersion,
					FileVersion = numericVersion,
					InformationalVersion = version,
					Company = companyName,
					Copyright = string.Format("Copyright {0} - {1}", companyName,  DateTime.Now.Year),
					});
		   }
    });

Task("Build")
    .Does(() =>
    {
		DirectoryPath vsLatest  = VSWhereLatest();
		FilePath msBuildPathX64 = (vsLatest==null)
                            ? null
                            : vsLatest.CombineWithFilePath("./MSBuild/15.0/Bin/MSBuild.exe");

		Information(string.Format("MSBuild Path is {0}", msBuildPathX64));

		var solutionPath = slnFiles[0];

		DotNetCoreBuild(solutionPath,
				new DotNetCoreBuildSettings
				{
					Configuration = configuration
				});
    });

Task("UnitTest")
    .Does(() =>
    {
  
	   CreateDirectory("./testresults");

	   CleanDirectory("./testresults");


	   // Run the NUnit Tests

	   NUnit3("./**/bin/" + configuration + "/net*/*Tests*.dll", new NUnit3Settings {
			Results = new[] { new NUnit3Result { FileName = "./testresults/UnitTestResults.xml", Format = "nunit2" } },
			Where = "cat == UnitTest",
			Labels = NUnit3Labels.On,
			Process = NUnit3ProcessOption.InProcess,
			EnvironmentVariables = new Dictionary<string, string> { { "VisualStudioVersion", "15.0" } }
        });
    });

Task("TestAll")
	.IsDependentOn("Build")
    .IsDependentOn("Package")
    .Does(() =>
    {

		CreateDirectory("./testresults");

	   CleanDirectory("./testresults");


	   // Run the NUnit Tests

	   NUnit3("./**/bin/" + configuration + "/net*/*Test*.dll", new NUnit3Settings {
			Results = new[] { new NUnit3Result { FileName = "./testresults/TestAllResults.xml", Format = "nunit2" } },
			Labels = NUnit3Labels.On,
			Process = NUnit3ProcessOption.InProcess,
			EnvironmentVariables = new Dictionary<string, string> { { "VisualStudioVersion", "15.0" } }
        });
    });

Task("Package")
    .Does(() =>
    {
		CreateDirectory("./NuGet/FileSystemWatcherAlts");
		CleanDirectories("./NuGet/FileSystemWatcherAlts");

		var nugetFilePaths = GetFiles("./BT.ThirdParty.FileSystemWatcherAlts.nuspec");
		var nuGetPackSettings   = new NuGetPackSettings {
									Version = pkgVersion,
									BasePath                = string.Format("./FileSystemWatcherAlts/bin/{0}", configuration),
									OutputDirectory         = nupkgPublishPath
								};
		
		NuGetPack(nugetFilePaths, nuGetPackSettings);
    });

	
Task("Publish")
	.Does(() =>
	{
		if (isMergeBuild)
		{
			Information("The 'isMergeBuild' specified. No packages were published.");
			return;
		}

		if (!publishPackages)
		{
			Information("The 'publishPackages' parameter not specified. No packages were published.");
			return;
		}

		if (!Jenkins.IsRunningOnJenkins)
		{
			Information("Only Jenkin's jobs are allowed to publish packages. No packages were published.");
			return;
		}

		if (gitVersion.BranchName != "origin/master" && gitVersion.BranchName != "origin/develop" && !IsReleaseBranch(gitVersion.BranchName))
		{
			Warning($"Publish not performed. Packages must originate from 'origin/develop' or 'origin/release-#.#' branches. Current branch: {gitVersion.BranchName}");
			return;
		}

		var pkgRepo = "http://ssnj-artifact01.billtrust.local:8081/artifactory/api/nuget/vuebillnuget";
		
		var nupkgs = GetFiles(nupkgPublishPath + "/*.nupkg");
		if (nupkgs.Count > 0)
		{
			NuGetPush(nupkgs, new NuGetPushSettings {
				Source = pkgRepo,
				ApiKey = pkgRepoApiKey,
				Verbosity = NuGetVerbosity.Detailed
			});
		} 
	});

Task("Full")
	.IsDependentOn("RealClean")
	.IsDependentOn("Version")
	.IsDependentOn("Build")
	.IsDependentOn("Package")
	.IsDependentOn("UnitTest")
	.IsDependentOn("Publish")
	.Does(() => 
	{
	});


Teardown(context =>
{
	Information("Reverting temporary 'AssemblyInfo.cs' files by running command: 'git checkout -- */AssemblyInfo.cs'");

	StartProcess("git", "checkout -- */AssemblyInfo.cs");
});

RunTarget(target);