using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using System.Text.RegularExpressions;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using Newtonsoft.Json;

private bool IsReleaseBranch(string branchName)
{
	return Regex.Match(branchName, @"origin\/release-\d+\.\d+").Success;
}

private string ConsolidatePackageManifests(FilePathCollection packageManifests)
{
	Information("Optimizing nuget restoration by consolidating '{0}' individual packages.config files into a single file.", packageManifests.Count);

	var nugets = packageManifests
                .Select(path =>
                {
                    var doc = new XmlDocument();
                    doc.Load(path.FullPath);
                    return doc;
                })
				.SelectMany(doc => doc.SelectNodes("/packages/package").OfType<XmlNode>()
                    .Select(node => new Tuple<string, string>(
						node.Attributes["id"].Value,
						node.Attributes["version"].Value)
					)
				).ToList();

	var nugetsDedup = nugets.Distinct(new NugetComparerIgnoreCase()).ToList();
	
	var tempXml = new XmlDocument();
	var xmldecl = tempXml.CreateXmlDeclaration("1.0", "utf-8", null);
	var root = tempXml.DocumentElement;
	tempXml.InsertBefore(xmldecl, root);
	
	var packagesNode = tempXml.CreateElement("packages");
	tempXml.AppendChild(packagesNode);
	
	foreach (var tuple in nugetsDedup)
	{
	    var packageNode = tempXml.CreateElement("package");
	    packageNode.SetAttribute("id", tuple.Item1);
	    packageNode.SetAttribute("version", tuple.Item2);
	    packagesNode.AppendChild(packageNode);
	}
	
	var tempPath = System.IO.Path.GetTempPath() + "packages.config";
	
	tempXml.Save(tempPath);
	
	Information("Saved temp file here: {0}", tempPath);
	
	foreach (var tuple in nugetsDedup)
	{
	    Information("Nuget: {0}   Version: {1}", tuple.Item1, tuple.Item2);
	}
	
	return tempPath;
}

internal class NugetComparerIgnoreCase : IEqualityComparer<Tuple<string, string>>
{
    public bool Equals(Tuple<string, string> x, Tuple<string, string> y)
    {
        return y != null &&
               x != null &&
               x.Item1.Equals(y.Item1, StringComparison.InvariantCultureIgnoreCase) &&
               x.Item2.Equals(y.Item2, StringComparison.InvariantCultureIgnoreCase);
    }

    public int GetHashCode(Tuple<string, string> obj)
    {
        return obj.GetHashCode();
    }
}

private Tuple<string, string> GetFromLocalVersionFile()
{
	var values = System.IO.File.ReadAllText("./version.txt").Trim().Split(new char[] {';'}, StringSplitOptions.RemoveEmptyEntries);
	if (values.Count() > 2)
	{
		throw new InvalidOperationException("Will not process more than 2 parameters in version.txt");
	}

	if (values.Count() == 0)
	{
		throw new InvalidOperationException("Will not process empty version.txt");
	}

	//version number + suffix (optional)
	var result = new Tuple<string, string>(values[0], values.Count() == 2 ? values[1] : String.Empty );

	return result;
}

internal class GitlabTagClient
    {
        internal static void TagCommitWithVersion(string gitServer, string repositoryName, string commitHash, string version, string securityToken)
        {
            var httpClient = new HttpClient();

            httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            httpClient.DefaultRequestHeaders.Add("Private-Token", securityToken);



            // Get Project

            var projectsResponse = httpClient
                .GetAsync($"https://{gitServer}/api/v4/projects?search={repositoryName.ToLower()}")
                .GetAwaiter().GetResult();

            projectsResponse.EnsureSuccessStatusCode();

            var contentProjects = projectsResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult();

            var repository = JsonConvert.DeserializeObject<List<Repository>>(contentProjects).First(r =>
                r.name.Equals(repositoryName, StringComparison.InvariantCultureIgnoreCase));




            // Does Tag Already Exist

            var tagsResponse = httpClient
                .GetAsync($"https://{gitServer}/api/v4/projects/{repository.id}/repository/tags")
                .GetAwaiter().GetResult();

            tagsResponse.EnsureSuccessStatusCode();

            var contentTags = tagsResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult();

            var existing = JsonConvert.DeserializeObject<List<Tag>>(contentTags).FirstOrDefault(t =>
                t.name.Equals(version, StringComparison.InvariantCultureIgnoreCase));

            if (existing != null)
            {
                return;
            }



            // Create New Tag

            var dict = new Dictionary<string, string> { { "ref", commitHash }, { "tag_name", version } };

            var json = JsonConvert.SerializeObject(dict);

            var createTag = httpClient
                .PostAsync($"https://{gitServer}/api/v4/projects/{repository.id}/repository/tags",
                    new StringContent(json, Encoding.UTF8, "application/json"))
                .GetAwaiter().GetResult();

            createTag.EnsureSuccessStatusCode();
        }
        
        internal class Tag
        {
            public string name { get; set; }
            public Commit commit { get; set; }
        }

        internal class Commit
        {
            public string id { get; set; }
        }

        internal class Repository
        {
            public string name { get; set; }
            public long id { get; set; }
        }
    }
