﻿// Copyright 2017 the original author or authors.
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
// https://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Steeltoe.Initializr.TemplateEngine.Models;
using Steeltoe.Initializr.TemplateEngine.Utilities;
using Stubble.Core;
using Stubble.Core.Builders;
using Stubble.Extensions.JsonNet;

namespace Steeltoe.Initializr.TemplateEngine.Services.Mustache
{
    /// <summary>
    /// An implementation of a Dotnet Template service via Mustache (Stubble).
    /// </summary>
    public class MustacheTemplateService : ITemplateService
    {
        private Dictionary<string, string> FriendlyNames { get; set; }

        private readonly StubbleVisitorRenderer _stubble;
        private readonly ILogger<MustacheTemplateService> _logger;
        private readonly string _templatePath;
        private readonly MustacheConfig _mustacheConfig;

        public MustacheTemplateService(IConfiguration configuration, ILogger<MustacheTemplateService> logger)
            : this(logger)
        {
            configuration.Bind(this); // Get friendlyNames
        }

        public MustacheTemplateService(ILogger<MustacheTemplateService> logger)
        {
            _stubble = new StubbleBuilder()
                .Configure(settings => settings.AddJsonNet())
                .Build();
            _logger = logger;
            _templatePath = Path.Join(AppDomain.CurrentDomain.BaseDirectory, "templates", "Mustache");
            _mustacheConfig = new MustacheConfig(_logger, _templatePath);
        }

        public async Task<byte[]> GenerateProjectArchiveAsync(GeneratorModel model)
        {
            return await Task.Run(() => GenerateProjectArchive(model)).ConfigureAwait(false);
        }

        public async Task<List<KeyValuePair<string, string>>> GenerateProjectFiles(GeneratorModel model)
        {
            var templateKey = new TemplateKey(model.SteeltoeVersion, model.TargetFramework, model.Template);
            if (!_mustacheConfig.GetTemplateKeys().Contains(templateKey))
            {
                throw new InvalidDataException($"Template with Name[{model.Template}] and Framework[{model.TargetFramework}] doesn't exist");
            }

            Dictionary<string, string> dataView;
            using (Timing.Over(_logger, "GetDataView"))
            {
                dataView = await _mustacheConfig.GetDataView(templateKey, model);
            }

            var listOfFiles = new List<KeyValuePair<string, string>>();
            using (Timing.Over(_logger, "Rendering files"))
            {
                foreach (var file in _mustacheConfig.GetFilteredSourceSets(dataView, templateKey))
                {
                    if (file.Name.EndsWith(".csproj"))
                    {
                        var fileName = file.Name.Replace("ReplaceMe", model.ProjectName ?? "SteeltoeExample");
                        var output = Render(file.Name, file.Text, dataView);
                        listOfFiles.Add(new KeyValuePair<string, string>(fileName, output));
                    }
                    else
                    {
                        var output = Render(file.Name, file.Text, dataView);
                        listOfFiles.Add(new KeyValuePair<string, string>(file.Name, output));
                    }
                }
            }

            return listOfFiles;
        }

        public List<TemplateViewModel> GetAvailableTemplates()
        {
            return _mustacheConfig.GetTemplateKeys()
                .Select(templateKey => new TemplateViewModel
                {
                    SteeltoeVersion = templateKey.Steeltoe,
                    TargetFramework = templateKey.Framework,
                    Name = templateKey.Template,
                    ShortName = templateKey.Template,
                    Language = "C#",
                    Tags = "Web/Microservice",
                })
                .ToList();
        }

        public List<ProjectDependency> GetDependencies(string steeltoe, string framework, string template)
        {
            var list = GetAvailableTemplates();
            var selectedTemplate = list.FirstOrDefault(x => x.ShortName == template);

            if (selectedTemplate == null)
            {
                throw new InvalidDataException($"Could not find template with name {template} ");
            }

            var config = _mustacheConfig.GetSchema(new TemplateKey(steeltoe, framework, template));
            return config.Params
                .Where(p => p.Description.ToLower().Contains("steeltoe"))
                .Select(p => new ProjectDependency
                {
                    Name = p.FriendlyName ?? GetFriendlyName(p.Name),
                    ShortName = p.Name,
                    Description = p.Description,
                }).ToList();
        }

        private async Task<byte[]> GenerateProjectArchive(GeneratorModel model)
        {
            byte[] archiveBytes;
            var listOfFiles = await GenerateProjectFiles(model);

            using (var memoryStream = new MemoryStream())
            {
                using (var archive = new ZipArchive(memoryStream, ZipArchiveMode.Create, true))
                {
                    foreach (var (key, value) in listOfFiles)
                    {
                        _logger.LogDebug(key);
                        var ef = archive.CreateEntry(key, CompressionLevel.Optimal);
                        using (var entryStream = ef.Open())
                        using (var fileToCompress = new MemoryStream(Encoding.UTF8.GetBytes(value)))
                        {
                            ef.ExternalAttributes = 27262976; // RW_(Owner)/R__(Group)/___(Other)
                            fileToCompress.CopyTo(entryStream);
                        }
                    }
                }

                archiveBytes = memoryStream.ToArray();
            }

            return archiveBytes;
        }

        private string Render(string name, string input, object view)
        {
            try
            {
                return _stubble.Render(input, view);
            }
            catch (Exception ex)
            {
                throw new Exception("Error rendering " + name, ex);
            }
        }

        private string GetFriendlyName(string name)
        {
            return FriendlyNames?.ContainsKey(name) == true ? FriendlyNames[name] : name;
        }
    }
}
