﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace NuGet
{
    public class PackageSourceDiscovery : IHttpClientEvents
    {
        private const string DefaultUserAgentClient = "NuGet Core";
        private static readonly XNamespace RsdNamespace = "http://archipelago.phrasewise.com/rsd";
        private static readonly XNamespace DcNamespace = "http://purl.org/dc/elements/1.1/";

        public event EventHandler<ProgressEventArgs> ProgressAvailable = delegate { };
        public event EventHandler<WebRequestEventArgs> SendingRequest = delegate { };

        public virtual IEnumerable<PackageSourceDiscoveryDocument> FetchDiscoveryDocuments(Uri uri)
        {
            return FetchDiscoveryDocuments(uri, null);
        }

        public virtual IEnumerable<PackageSourceDiscoveryDocument> FetchDiscoveryDocuments(Uri uri, string title)
        {
            if (uri == null)
            {
                throw new ArgumentNullException("uri");
            }

            // Download from given URL
            string data = DownloadAsString(uri);

            IEnumerable<PackageSourceDiscoveryDocument> discoveryDocuments = DiscoverPackageSourcesFromHtml(uri, title, data);

            if (!discoveryDocuments.Any())
                discoveryDocuments = DiscoverPackageSourcesFromFeed(uri, title, data);

            if (!discoveryDocuments.Any())
                discoveryDocuments = DiscoverPackageSourcesFromRsd(data);

            return discoveryDocuments;
        }

        protected virtual IEnumerable<PackageSourceDiscoveryDocument> DiscoverPackageSourcesFromHtml(Uri uri, string title, string data)
        {
            var discoveryDocuments = new List<PackageSourceDiscoveryDocument>();

            // Parse HTML for <link> tags
            var linkMatches = Regex.Matches(data, @"<\s*link\s*[^>]*?href\s*=\s*[""']*([^""'>]+)[^>]*?>", RegexOptions.IgnoreCase);
            if (linkMatches.Count > 0)
            {
                XDocument htmlDocument = XDocument.Parse(
                    string.Format(@"<?xml version=""1.0""?><links>{0}</links>", String.Join("", linkMatches.Cast<Match>().Select(m => m.Value))));

                foreach (XElement link in htmlDocument.Descendants("link"))
                {
                    if (link.HasAttributes && string.Equals(AttributeValueOrNull(link.Attribute("rel")) ?? "", "nuget", StringComparison.InvariantCultureIgnoreCase))
                    {
                        var linkHref = new Uri(link.Attribute("href").Value);
                        var linkTitle = AttributeValueOrNull(link.Attribute("title"));

                        discoveryDocuments.AddRange(FetchDiscoveryDocuments(linkHref, linkTitle));
                    }
                }
            }
            return discoveryDocuments;
        }

        protected virtual IEnumerable<PackageSourceDiscoveryDocument> DiscoverPackageSourcesFromFeed(Uri uri, string title, string data)
        {
            // TODO: should we throw a different exception if XML can not be parsed?
            XDocument xml = XDocument.Parse(data);

            // If a feed is given, return the feed
            if (xml.Root != null && xml.Root.Name.LocalName == "service")
            {
                return new[]
                    {
                        new PackageSourceDiscoveryDocument(title ?? uri.PathAndQuery, uri.ToString())
                    };
            }
            return new PackageSourceDiscoveryDocument[] { };
        }

        private IEnumerable<PackageSourceDiscoveryDocument> DiscoverPackageSourcesFromRsd(string data)
        {
            var discoveryDocuments = new List<PackageSourceDiscoveryDocument>();

            // TODO: should we throw a different exception if XML can not be parsed?
            XDocument xml = XDocument.Parse(data);

            // If an RSD is given, parse it
            if (xml.Root != null && xml.Root.Name == RsdNamespace + "rsd")
            {
                foreach (XElement xmlService in xml.Descendants(RsdNamespace + "service"))
                {
                    var discoveryDocument = new PackageSourceDiscoveryDocument();
                    discoveryDocument.EngineName = ElementValueOrNull(xmlService.Element(RsdNamespace + "engineName"));
                    discoveryDocument.EngineLink = ElementValueOrNull(xmlService.Element(RsdNamespace + "engineLink"));
                    discoveryDocument.HomePageLink = ElementValueOrNull(xmlService.Element(RsdNamespace + "homePageLink"));
                    discoveryDocument.Identifier = ElementValueOrNull(xmlService.Element(DcNamespace + "identifier"));
                    discoveryDocument.Owner = ElementValueOrNull(xmlService.Element(DcNamespace + "owner"));
                    discoveryDocument.Creator = ElementValueOrNull(xmlService.Element(DcNamespace + "creator"));
                    discoveryDocument.Title = ElementValueOrNull(xmlService.Element(DcNamespace + "title"));
                    discoveryDocument.Description = ElementValueOrNull(xmlService.Element(DcNamespace + "description"));

                    foreach (XElement xmlApi in xmlService.Descendants(RsdNamespace + "api"))
                    {
                        var endpoint = new PackageSourceEndpoint();
                        endpoint.ApiLink = AttributeValueOrNull(xmlApi.Attribute("apiLink"));
                        endpoint.Preferred = bool.Parse(AttributeValueOrNull(xmlApi.Attribute("preferred")) ?? "false");
                        endpoint.Name = AttributeValueOrNull(xmlApi.Attribute("name"));

                        foreach (XElement xmlSetting in xmlApi.Descendants(RsdNamespace + "setting"))
                        {
                            endpoint.Settings.Add(AttributeValueOrNull(xmlSetting.Attribute("name")), ElementValueOrNull(xmlSetting) ?? "");
                        }

                        discoveryDocument.Endpoints.Add(endpoint);
                    }

                    discoveryDocuments.Add(discoveryDocument);
                }
            }
            return discoveryDocuments;
        }

        protected string AttributeValueOrNull(XAttribute attribute)
        {
            return attribute != null ? attribute.Value : null;
        }

        protected string ElementValueOrNull(XElement element)
        {
            return element != null ? element.Value : null;
        }

        protected virtual string DownloadAsString(Uri uri)
        {
            var httpClient = SetupHttpClient(uri);
            using (var memoryStream = new MemoryStream())
            {
                EventHandler<WebRequestEventArgs> beforeSendingRequesthandler = (sender, e) => OnSendingRequest(e.Request);

                try
                {
                    httpClient.SendingRequest += beforeSendingRequesthandler;

                    httpClient.DownloadData(memoryStream);
                    memoryStream.Seek(0, SeekOrigin.Begin);
                    using (var streamReader = new StreamReader(memoryStream))
                    {
                        return streamReader.ReadToEnd();
                    }
                }
                finally
                {
                    httpClient.SendingRequest -= beforeSendingRequesthandler;
                }
            }
        }

        protected virtual HttpClient SetupHttpClient(Uri uri)
        {
            return new HttpClient(uri)
            {
                UserAgent = HttpUtility.CreateUserAgentString(DefaultUserAgentClient),
                AcceptCompression = true
            };
        }

        private void OnSendingRequest(WebRequest webRequest)
        {
            SendingRequest(this, new WebRequestEventArgs(webRequest));
        }
    }
}