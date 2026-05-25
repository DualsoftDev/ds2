using System;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using Promaker.Dialogs.ConfigEditor;
using Xunit;

namespace Promaker.Tests;

public sealed class ConfigEditorViewModelTests
{
    [Fact]
    public void SaveCommand_with_dirty_RawJson_saves_full_json_section()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ds2-config-editor-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, MinimalConfigJson, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            var vm = new ConfigEditorViewModel(path);
            vm.RawJson = MinimalConfigJson.Replace("\"Enabled\": true", "\"Enabled\": false", StringComparison.Ordinal);

            vm.SaveCommand.Execute(null);

            var saved = JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8))!;
            Assert.False(saved["UserTagRules"]!["Enabled"]!.GetValue<bool>());
            Assert.Equal("Error", saved["UserTagRules"]!["Rules"]![0]!["LogLevel"]!.GetValue<string>());
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void Preview_uses_current_basic_naming_options()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ds2-config-editor-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, MinimalConfigJson, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            var vm = new ConfigEditorViewModel(path)
            {
                IoTypeTokens = "Q\nQX\nI\nIX",
                CompoundSuffixes = "ADV\nRET",
                PreviewSampleSymbol = "CV_1_ADV"
            };

            Assert.Equal("CV", vm.PreviewFlowName);
            Assert.Equal("CV", vm.PreviewWorkName);
            Assert.Equal("CV_1", vm.PreviewDeviceName);
            Assert.Equal("ADV", vm.PreviewApiName);
            Assert.Equal("CV_1.ADV", vm.PreviewCallName);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    private const string MinimalConfigJson = """
    {
      "Common": {
        "MappingSets": [
          {
            "Name": "test",
            "DeviceKeywords": ["*Foo*"],
            "Apis": [
              {
                "Name": "ADV",
                "OutputKeywords": ["O"],
                "InputKeywords": ["I"]
              }
            ],
            "OutputAddressPatterns": ["Y*"],
            "InputAddressPatterns": ["X*"]
          }
        ]
      },
      "Vendors": {},
      "SymmetryRules": [],
      "ExplicitMappings": [],
      "FilterExclusions": {
        "Description": "",
        "DeviceKeywords": [],
        "ApiKeywords": [],
        "FlowKeywords": []
      },
      "FlowInclusions": {
        "Description": "",
        "Flows": []
      },
      "ApiNaming": {},
      "WorkNaming": {},
      "NodeConnectionRules": {},
      "DeviceNaming": {},
      "DisplayNaming": {},
      "UserTagRules": {
        "Description": "test",
        "Enabled": true,
        "Rules": [
          {
            "Name": "Errors",
            "Enabled": true,
            "LogLevel": "Error",
            "ValueType": "Bit",
            "MatchOp": "RisingEdge",
            "MatchValue": "1",
            "Directions": ["Input", "Memory"],
            "AddressPatterns": ["X*", "M*"],
            "NamePatterns": ["*ERR*"],
            "CommentPatterns": ["*ERROR*"],
            "ExcludeAddressPatterns": [],
            "ExcludeNamePatterns": [],
            "ExcludeCommentPatterns": []
          }
        ]
      }
    }
    """;
}
