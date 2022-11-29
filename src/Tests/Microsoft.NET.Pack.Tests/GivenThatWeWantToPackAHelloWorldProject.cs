﻿// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.NET.Build.Tasks;
using System.Xml.Linq;
using Microsoft.NET.TestFramework;
using Microsoft.NET.TestFramework.Assertions;
using Microsoft.NET.TestFramework.Commands;
using Microsoft.NET.TestFramework.ProjectConstruction;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;
using Microsoft.DotNet.Tools;
using Microsoft.DotNet.Cli;

namespace Microsoft.NET.Pack.Tests
{
    public class GivenThatWeWantToPackAHelloWorldProject : SdkTest
    {
        public GivenThatWeWantToPackAHelloWorldProject(ITestOutputHelper log) : base(log)
        {
        }

        [Fact]
        public void It_packs_successfully()
        {
            var helloWorldAsset = _testAssetsManager
                .CopyTestAsset("HelloWorld", "PackHelloWorld")
                .WithSource();

            var packCommand = new PackCommand(Log, helloWorldAsset.TestRoot);

            packCommand
                .Execute()
                .Should()
                .Pass();

            //  Validate the contents of the NuGet package by looking at the generated .nuspec file, as that's simpler
            //  than unzipping and inspecting the .nupkg
            string nuspecPath = packCommand.GetIntermediateNuspecPath();
            var nuspec = XDocument.Load(nuspecPath);

            var ns = nuspec.Root.Name.Namespace;
            XElement filesSection = nuspec.Root.Element(ns + "files");

            var fileTargets = filesSection.Elements().Select(files => files.Attribute("target").Value).ToList();

            var expectedFileTargets = new[]
            {
                $@"lib\{ToolsetInfo.CurrentTargetFramework}\HelloWorld.runtimeconfig.json",
                $@"lib\{ToolsetInfo.CurrentTargetFramework}\HelloWorld.dll"
            }.Select(p => p.Replace('\\', Path.DirectorySeparatorChar));

            fileTargets.Should().BeEquivalentTo(expectedFileTargets);
        }

        [Fact]
        public void It_fails_if_nobuild_was_requested_but_build_was_invoked()
        {
            var testProject = new TestProject()
            {
                Name = "InvokeBuildOnPack",
                TargetFrameworks = ToolsetInfo.CurrentTargetFramework,
                IsExe = true
            };

            var testAsset = _testAssetsManager.CreateTestProject(testProject, testProject.Name)
                .WithProjectChanges(project =>
                {
                    project.Root.Add(XElement.Parse(@"<Target Name=""InvokeBuild"" DependsOnTargets=""Build"" BeforeTargets=""Pack"" />"));
                });

            new BuildCommand(testAsset)
                .Execute()
                .Should()
                .Pass();

            new PackCommand(Log, Path.Combine(testAsset.TestRoot, testProject.Name))
                .Execute("/p:NoBuild=true")
                .Should()
                .Fail()
                .And
                .HaveStdOutContaining("NETSDK1085");
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void It_packs_with_release_if_PackRelease_property_set(bool optedOut)
        {
            if (optedOut)
            {
                Environment.SetEnvironmentVariable(EnvironmentVariableNames.DISABLE_PUBLISH_AND_PACK_RELEASE, "true");
            }

            var helloWorldAsset = _testAssetsManager
               .CopyTestAsset("HelloWorld", $"PackReleaseBasicTest-Opted-Out-{optedOut}")
               .WithSource();

            File.WriteAllText(helloWorldAsset.Path + "/Directory.Build.props", "<Project><PropertyGroup><PackRelease>true</PackRelease></PropertyGroup></Project>");

            var packCommand = new DotnetPackCommand(Log, helloWorldAsset.TestRoot);

            packCommand
                .Execute()
                .Should()
                .Pass();

            var expectedAssetPath = Path.Combine(helloWorldAsset.Path, "bin", optedOut ? "Debug" : "Release", "HelloWorld.1.0.0.nupkg");
            Assert.True(File.Exists(expectedAssetPath));

            Environment.SetEnvironmentVariable(EnvironmentVariableNames.DISABLE_PUBLISH_AND_PACK_RELEASE, null);
        }

        [Fact]
        public void It_packs_with_release_if_PackRelease_property_set_in_csproj()
        {
            var helloWorldAsset = _testAssetsManager
               .CopyTestAsset("HelloWorld", "PackReleaseHelloWorld")
               .WithSource()
               .WithProjectChanges(project =>
               {
                   var ns = project.Root.Name.Namespace;
                   var propertyGroup = project.Root.Elements(ns + "PropertyGroup").First();
                   propertyGroup.Add(new XElement(ns + "PackRelease", "true"));
               });

            var packCommand = new DotnetPackCommand(Log, helloWorldAsset.TestRoot);

            packCommand
                .Execute()
                .Should()
                .Pass();

            var expectedAssetPath = Path.Combine(helloWorldAsset.Path, "bin", "Release", "HelloWorld.1.0.0.nupkg");
            Assert.True(File.Exists(expectedAssetPath));
        }

        [Theory]
        [InlineData("net7.0")]
        [InlineData("net8.0")]
        public void It_packs_with_release_by_default_in_net_8_but_not_in_net_7(string tfm)
        {
            var helloWorldAsset = _testAssetsManager
               .CopyTestAsset("HelloWorld", $"PackReleaseDefault-{tfm}")
               .WithSource()
               .WithTargetFramework(tfm);

            new DotnetPackCommand(Log, helloWorldAsset.TestRoot)
                .Execute()
                .Should()
                .Pass();

            var expectedAssetPath = Path.Combine(helloWorldAsset.Path, "bin", tfm == "net7.0" ? "Debug" : "Release", "HelloWorld.1.0.0.nupkg");
            Assert.True(File.Exists(expectedAssetPath));
        }

        [Fact]
        public void It_packs_with_Release_on_all_TargetFrameworks_If_8_or_above_is_included()
        {
            var testProject = new TestProject()
            {
                IsExe = true,
                TargetFrameworks = "net7.0;net8.0"
            };
            testProject.RecordProperties("Configuration");

            var testAsset = _testAssetsManager.CreateTestProject(testProject);

            new DotnetPackCommand(Log)
                .WithWorkingDirectory(Path.Combine(testAsset.TestRoot, testProject.Name))
                .Execute()
                .Should()
                .Pass();

            var properties = testProject.GetPropertyValues(testAsset.TestRoot, targetFramework: "net7.0", configuration: "Release"); // this will fail if configuration is debug and TFM code didn't work.
            var finalConfiguration = properties["Configuration"];
            Assert.True(finalConfiguration == "Release");
        }

        [Fact]
        public void It_fails_with_conflicting_PackRelease_values_in_solution_file()
        {
            var slnDir = _testAssetsManager
               .CopyTestAsset("TestAppWithSlnUsingPublishReleaseConflictingValues") // this also contains conflicting PackRelease values.
               .WithSource()
               .Path;

            new DotnetCommand(Log)
                .WithWorkingDirectory(slnDir)
                .Execute("dotnet", "pack")
                .Should()
                .Fail()
                .And
                .HaveStdOutContaining(string.Format(Strings.SolutionProjectConfigurationsConflict, "PackRelease"));
        }

        [Fact]
        public void A_PackRelease_property_does_not_affect_other_commands_besides_pack()
        {
            var tfm = "net8.0";
            var helloWorldAsset = _testAssetsManager
               .CopyTestAsset("HelloWorld")
               .WithSource()
               .WithTargetFramework(tfm);

            File.WriteAllText(helloWorldAsset.Path + "/Directory.Build.props", "<Project><PropertyGroup><PackRelease>false</PackRelease></PropertyGroup></Project>");

            var publishCommand = new DotnetPublishCommand(Log, helloWorldAsset.TestRoot);

            publishCommand
                .Execute()
                .Should()
                .Pass();

            var unexpectedAssetPath = Path.Combine(helloWorldAsset.Path, "bin", "Debug", tfm, "HelloWorld.dll");
            Assert.False(File.Exists(unexpectedAssetPath));
            var expectedAssetPath = Path.Combine(helloWorldAsset.Path, "bin", "Release", tfm, "HelloWorld.dll");
            Assert.True(File.Exists(expectedAssetPath));
        }
    }
}
