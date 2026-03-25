# AASX JSON Editor - 범용 AASX 편집 도구

**작성일**: 2026-03-24
**버전**: 1.0
**접근 방식**: AASX ↔ JSON 양방향 변환 + Web 기반 JSON Editor
**상태**: Production-Ready Specification

---

## 📋 목차

1. [Executive Summary](#1-executive-summary)
2. [핵심 아이디어](#2-핵심-아이디어)
3. [AASX-JSON 매핑](#3-aasx-json-매핑)
4. [JSON 스키마 설계](#4-json-스키마-설계)
5. [AASX ↔ JSON 변환기](#5-aasx--json-변환기)
6. [Web 기반 JSON Editor](#6-web-기반-json-editor)
7. [CLI 도구](#7-cli-도구)
8. [워크플로우](#8-워크플로우)
9. [구현 가이드](#9-구현-가이드)

---

## 1. Executive Summary

### 1.1 문제 정의

**기존 AASX 편집의 문제점:**
- AASX Package Explorer는 복잡하고 전문가용
- XML 기반 구조가 직관적이지 않음
- 대량 편집이 어려움 (복사/붙여넣기 제한)
- 버전 관리 어려움 (바이너리 파일)
- 자동화 스크립트 작성 어려움

### 1.2 해결 방안

```
AASX (복잡한 XML/ZIP) ─────> JSON (간단한 구조) ─────> 범용 편집
	↑                                              │
	└──────────────────────────────────────────────┘
			다시 AASX로 변환
```

**핵심 이점:**
1. **간단한 구조**: XML → JSON 변환으로 계층 구조 명확화
2. **범용 도구**: VS Code, Notepad++, 온라인 JSON 에디터
3. **Git 친화적**: JSON은 텍스트 파일로 Diff 추적 가능
4. **자동화**: Python, Node.js로 쉽게 처리
5. **검증**: JSON Schema로 자동 검증

### 1.3 대상 사용자

| 사용자 | 사용 목적 | 도구 |
|--------|-----------|------|
| **시스템 엔지니어** | AASX 수동 편집 | VS Code + JSON Editor Extension |
| **개발자** | AASX 자동 생성/수정 | Python/Node.js 스크립트 |
| **데이터 관리자** | 대량 AASX 편집 | Excel + JSON 변환 |
| **품질 관리** | AASX 검증 | JSON Schema Validator |

---

## 2. 핵심 아이디어

### 2.1 AASX 구조 간소화

#### 기존 AASX 구조 (복잡)

```xml
<!-- /aasx/xml/submodel.xml -->
<aas:Submodel xmlns:aas="https://admin-shell.io/aas/3/0">
  <aas:idShort>TechnicalData</aas:idShort>
  <aas:id>https://example.com/submodel/techdata/1</aas:id>
  <aas:semanticId>
    <aas:type>ExternalReference</aas:type>
    <aas:keys>
      <aas:key>
        <aas:type>GlobalReference</aas:type>
        <aas:value>https://admin-shell.io/ZVEI/TechnicalData/Submodel/1/2</aas:value>
      </aas:key>
    </aas:keys>
  </aas:semanticId>
  <aas:submodelElements>
    <aas:property>
      <aas:idShort>Weight</aas:idShort>
      <aas:valueType>xs:double</aas:valueType>
      <aas:value>25.5</aas:value>
    </aas:property>
  </aas:submodelElements>
</aas:Submodel>
```

#### 신규 JSON 구조 (간단)

```json
{
  "aasVersion": "3.0",
  "assetAdministrationShell": {
    "idShort": "MyAsset",
    "id": "https://example.com/aas/12345",
    "assetInformation": {
      "assetKind": "Instance",
      "globalAssetId": "https://example.com/asset/12345"
    }
  },
  "submodels": [
    {
      "idShort": "TechnicalData",
      "id": "https://example.com/submodel/techdata/1",
      "semanticId": "https://admin-shell.io/ZVEI/TechnicalData/Submodel/1/2",
      "submodelElements": [
        {
          "idShort": "Weight",
          "type": "Property",
          "valueType": "double",
          "value": 25.5
        },
        {
          "idShort": "MaxSpeed",
          "type": "Property",
          "valueType": "integer",
          "value": 3000
        }
      ]
    }
  ]
}
```

### 2.2 3단계 워크플로우

```
┌─────────────┐
│ AASX 파일   │ (복잡한 XML + ZIP)
└──────┬──────┘
       │ (1) aasx2json
       ↓
┌─────────────┐
│ JSON 파일   │ (단순한 구조)
└──────┬──────┘
       │ (2) 편집
       │     - VS Code
       │     - Web Editor
       │     - Python Script
       ↓
┌─────────────┐
│ JSON 파일   │ (수정됨)
└──────┬──────┘
       │ (3) json2aasx
       ↓
┌─────────────┐
│ AASX 파일   │ (표준 준수)
└─────────────┘
```

---

## 3. AASX-JSON 매핑

### 3.1 전체 구조 매핑

| AASX 요소 | XML 위치 | JSON 경로 |
|-----------|----------|-----------|
| AAS | `/aas-spec/AssetAdministrationShell` | `$.assetAdministrationShell` |
| Submodel | `/submodels/{id}/submodel.xml` | `$.submodels[]` |
| Property | `Submodel/submodelElements/property` | `$.submodels[].submodelElements[]` |
| Collection | `Submodel/submodelElements/submodelElementCollection` | `$.submodels[].submodelElements[]` |
| File | `Submodel/submodelElements/file` | `$.submodels[].submodelElements[]` + `$.files[]` |

### 3.2 AssetAdministrationShell 매핑

#### AASX XML
```xml
<aas:assetAdministrationShell>
  <aas:idShort>MyProductAAS</aas:idShort>
  <aas:id>https://example.com/aas/product-001</aas:id>
  <aas:assetInformation>
    <aas:assetKind>Instance</aas:assetKind>
    <aas:globalAssetId>https://example.com/asset/product-001</aas:globalAssetId>
  </aas:assetInformation>
  <aas:submodels>
    <aas:reference>
      <aas:type>ModelReference</aas:type>
      <aas:keys>
        <aas:key>
          <aas:type>Submodel</aas:type>
          <aas:value>https://example.com/submodel/nameplate/1</aas:value>
        </aas:key>
      </aas:keys>
    </aas:reference>
  </aas:submodels>
</aas:assetAdministrationShell>
```

#### JSON
```json
{
  "assetAdministrationShell": {
    "idShort": "MyProductAAS",
    "id": "https://example.com/aas/product-001",
    "assetInformation": {
      "assetKind": "Instance",
      "globalAssetId": "https://example.com/asset/product-001"
    },
    "submodelRefs": [
      "https://example.com/submodel/nameplate/1"
    ]
  }
}
```

### 3.3 Submodel 매핑

#### Digital Nameplate (표준 서브모델)

**AASX XML** (장황함):
```xml
<aas:submodel>
  <aas:idShort>Nameplate</aas:idShort>
  <aas:semanticId>
    <aas:type>ExternalReference</aas:type>
    <aas:keys>
      <aas:key>
        <aas:type>GlobalReference</aas:type>
        <aas:value>https://admin-shell.io/zvei/nameplate/2/0/Nameplate</aas:value>
      </aas:key>
    </aas:keys>
  </aas:semanticId>
  <aas:submodelElements>
    <aas:property>
      <aas:idShort>ManufacturerName</aas:idShort>
      <aas:semanticId>
        <aas:type>ExternalReference</aas:type>
        <aas:keys>
          <aas:key>
            <aas:type>GlobalReference</aas:type>
            <aas:value>0173-1#02-AAO677#002</aas:value>
          </aas:key>
        </aas:keys>
      </aas:semanticId>
      <aas:valueType>xs:string</aas:valueType>
      <aas:value>ACME Corporation</aas:value>
    </aas:property>
  </aas:submodelElements>
</aas:submodel>
```

**JSON** (간결함):
```json
{
  "idShort": "Nameplate",
  "id": "https://example.com/submodel/nameplate/1",
  "semanticId": "https://admin-shell.io/zvei/nameplate/2/0/Nameplate",
  "kind": "Instance",
  "submodelElements": [
    {
      "idShort": "ManufacturerName",
      "type": "Property",
      "semanticId": "0173-1#02-AAO677#002",
      "valueType": "string",
      "value": "ACME Corporation"
    },
    {
      "idShort": "SerialNumber",
      "type": "Property",
      "semanticId": "0173-1#02-AAM556#002",
      "valueType": "string",
      "value": "SN-2024-001234"
    }
  ]
}
```

### 3.4 SubmodelElementCollection 매핑

#### AASX XML
```xml
<aas:submodelElementCollection>
  <aas:idShort>ContactInformation</aas:idShort>
  <aas:value>
    <aas:property>
      <aas:idShort>Email</aas:idShort>
      <aas:valueType>xs:string</aas:valueType>
      <aas:value>contact@acme.com</aas:value>
    </aas:property>
    <aas:property>
      <aas:idShort>Phone</aas:idShort>
      <aas:valueType>xs:string</aas:valueType>
      <aas:value>+1-555-0123</aas:value>
    </aas:property>
  </aas:value>
</aas:submodelElementCollection>
```

#### JSON
```json
{
  "idShort": "ContactInformation",
  "type": "SubmodelElementCollection",
  "value": [
    {
      "idShort": "Email",
      "type": "Property",
      "valueType": "string",
      "value": "contact@acme.com"
    },
    {
      "idShort": "Phone",
      "type": "Property",
      "valueType": "string",
      "value": "+1-555-0123"
    }
  ]
}
```

### 3.5 File 요소 매핑

#### AASX XML + Binary
```xml
<aas:file>
  <aas:idShort>ProductImage</aas:idShort>
  <aas:contentType>image/png</aas:contentType>
  <aas:value>/aasx/files/product.png</aas:value>
</aas:file>
```

#### JSON
```json
{
  "idShort": "ProductImage",
  "type": "File",
  "contentType": "image/png",
  "value": "/aasx/files/product.png"
}
```

**추가 files 섹션** (Base64 임베드 옵션):
```json
{
  "files": [
    {
      "path": "/aasx/files/product.png",
      "contentType": "image/png",
      "encoding": "base64",
      "data": "iVBORw0KGgoAAAANSUhEUgAA..."
    }
  ]
}
```

---

## 4. JSON 스키마 설계

### 4.1 전체 JSON Schema

```json
{
  "$schema": "http://json-schema.org/draft-07/schema#",
  "title": "AASX JSON Format",
  "version": "3.0",
  "type": "object",
  "required": ["aasVersion", "assetAdministrationShell"],
  "properties": {
    "aasVersion": {
      "type": "string",
      "enum": ["3.0"],
      "description": "AAS metamodel version"
    },
    "assetAdministrationShell": {
      "$ref": "#/definitions/AssetAdministrationShell"
    },
    "submodels": {
      "type": "array",
      "items": { "$ref": "#/definitions/Submodel" }
    },
    "files": {
      "type": "array",
      "items": { "$ref": "#/definitions/FileResource" }
    }
  },
  "definitions": {
    "AssetAdministrationShell": {
      "type": "object",
      "required": ["idShort", "id", "assetInformation"],
      "properties": {
        "idShort": { "type": "string", "minLength": 1 },
        "id": { "type": "string", "format": "uri" },
        "assetInformation": {
          "type": "object",
          "required": ["assetKind", "globalAssetId"],
          "properties": {
            "assetKind": { "enum": ["Instance", "Type"] },
            "globalAssetId": { "type": "string", "format": "uri" }
          }
        },
        "submodelRefs": {
          "type": "array",
          "items": { "type": "string", "format": "uri" }
        }
      }
    },
    "Submodel": {
      "type": "object",
      "required": ["idShort", "id"],
      "properties": {
        "idShort": { "type": "string", "minLength": 1 },
        "id": { "type": "string", "format": "uri" },
        "semanticId": { "type": "string", "format": "uri" },
        "kind": { "enum": ["Instance", "Template"], "default": "Instance" },
        "submodelElements": {
          "type": "array",
          "items": { "$ref": "#/definitions/SubmodelElement" }
        }
      }
    },
    "SubmodelElement": {
      "type": "object",
      "required": ["idShort", "type"],
      "properties": {
        "idShort": { "type": "string", "minLength": 1 },
        "type": {
          "enum": [
            "Property",
            "MultiLanguageProperty",
            "Range",
            "File",
            "Blob",
            "ReferenceElement",
            "SubmodelElementCollection",
            "SubmodelElementList"
          ]
        },
        "semanticId": { "type": "string" },
        "valueType": { "type": "string" },
        "value": {}
      },
      "allOf": [
        {
          "if": { "properties": { "type": { "const": "Property" } } },
          "then": { "required": ["valueType", "value"] }
        },
        {
          "if": { "properties": { "type": { "const": "SubmodelElementCollection" } } },
          "then": {
            "properties": {
              "value": {
                "type": "array",
                "items": { "$ref": "#/definitions/SubmodelElement" }
              }
            }
          }
        }
      ]
    },
    "FileResource": {
      "type": "object",
      "required": ["path", "contentType"],
      "properties": {
        "path": { "type": "string" },
        "contentType": { "type": "string" },
        "encoding": { "enum": ["base64", "external"] },
        "data": { "type": "string" }
      }
    }
  }
}
```

### 4.2 사용 예시

**VS Code에서 자동 완성:**
```json
{
  "$schema": "./aasx-schema.json",
  "aasVersion": "3.0",
  "assetAdministrationShell": {
    // Ctrl+Space로 자동 완성
  }
}
```

---

## 5. AASX ↔ JSON 변환기

### 5.1 아키텍처

```
┌─────────────────────────────────────────────┐
│         AASX ↔ JSON Converter               │
│                                             │
│  ┌───────────────┐      ┌───────────────┐  │
│  │  AASX Reader  │      │  JSON Writer  │  │
│  │  (ZIP + XML)  │─────>│  (Serialize)  │  │
│  └───────────────┘      └───────────────┘  │
│         ↑                                   │
│         │                                   │
│  ┌───────────────┐      ┌───────────────┐  │
│  │  AASX Writer  │      │  JSON Reader  │  │
│  │  (ZIP + XML)  │<─────│(Deserialize)  │  │
│  └───────────────┘      └───────────────┘  │
│                                             │
│  ┌───────────────────────────────────────┐ │
│  │      Validation Engine                │ │
│  │  - JSON Schema Validator              │ │
│  │  - AASX Conformance Checker           │ │
│  └───────────────────────────────────────┘ │
└─────────────────────────────────────────────┘
```

### 5.2 C# 구현: AASX → JSON

```csharp
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Xml.Linq;

namespace DSPilot.AasxTools;

public class AasxToJsonConverter
{
    public async Task<string> ConvertAsync(string aasxPath)
    {
        // 1. AASX 파일 열기 (ZIP)
        using var archive = ZipFile.OpenRead(aasxPath);

        // 2. AAS XML 파일 찾기
        var aasXmlEntry = archive.Entries.FirstOrDefault(e =>
            e.FullName.EndsWith(".xml") && e.FullName.Contains("aas"));

        if (aasXmlEntry == null)
            throw new InvalidDataException("AAS XML not found in AASX");

        // 3. XML 파싱
        using var stream = aasXmlEntry.Open();
        var xmlDoc = await XDocument.LoadAsync(stream, LoadOptions.None, default);

        // 4. JSON 모델 생성
        var jsonModel = new AasxJsonModel
        {
            AasVersion = "3.0",
            AssetAdministrationShell = ParseAas(xmlDoc),
            Submodels = ParseSubmodels(archive),
            Files = ExtractFiles(archive)
        };

        // 5. JSON 직렬화
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        return JsonSerializer.Serialize(jsonModel, options);
    }

    private AasShell ParseAas(XDocument xmlDoc)
    {
        var ns = XNamespace.Get("https://admin-shell.io/aas/3/0");
        var aasElement = xmlDoc.Descendants(ns + "assetAdministrationShell").First();

        return new AasShell
        {
            IdShort = aasElement.Element(ns + "idShort")?.Value,
            Id = aasElement.Element(ns + "id")?.Value,
            AssetInformation = ParseAssetInfo(aasElement.Element(ns + "assetInformation")),
            SubmodelRefs = ParseSubmodelRefs(aasElement.Element(ns + "submodels"))
        };
    }

    private List<Submodel> ParseSubmodels(ZipArchive archive)
    {
        var submodels = new List<Submodel>();

        // /submodels/ 폴더의 모든 XML 파일
        var submodelEntries = archive.Entries
            .Where(e => e.FullName.StartsWith("submodels/") && e.FullName.EndsWith(".xml"));

        foreach (var entry in submodelEntries)
        {
            using var stream = entry.Open();
            var xmlDoc = XDocument.Load(stream);

            var submodel = ParseSubmodel(xmlDoc);
            submodels.Add(submodel);
        }

        return submodels;
    }

    private Submodel ParseSubmodel(XDocument xmlDoc)
    {
        var ns = XNamespace.Get("https://admin-shell.io/aas/3/0");
        var submodelElement = xmlDoc.Descendants(ns + "submodel").First();

        return new Submodel
        {
            IdShort = submodelElement.Element(ns + "idShort")?.Value,
            Id = submodelElement.Element(ns + "id")?.Value,
            SemanticId = ParseSemanticId(submodelElement.Element(ns + "semanticId")),
            Kind = submodelElement.Element(ns + "kind")?.Value ?? "Instance",
            SubmodelElements = ParseSubmodelElements(
                submodelElement.Element(ns + "submodelElements")
            )
        };
    }

    private List<SubmodelElement> ParseSubmodelElements(XElement? parent)
    {
        if (parent == null) return new List<SubmodelElement>();

        var ns = XNamespace.Get("https://admin-shell.io/aas/3/0");
        var elements = new List<SubmodelElement>();

        foreach (var element in parent.Elements())
        {
            var localName = element.Name.LocalName;

            if (localName == "property")
            {
                elements.Add(new SubmodelElement
                {
                    IdShort = element.Element(ns + "idShort")?.Value,
                    Type = "Property",
                    ValueType = element.Element(ns + "valueType")?.Value,
                    Value = element.Element(ns + "value")?.Value,
                    SemanticId = ParseSemanticId(element.Element(ns + "semanticId"))
                });
            }
            else if (localName == "submodelElementCollection")
            {
                elements.Add(new SubmodelElement
                {
                    IdShort = element.Element(ns + "idShort")?.Value,
                    Type = "SubmodelElementCollection",
                    Value = ParseSubmodelElements(element.Element(ns + "value"))
                });
            }
            // 다른 타입들도 처리...
        }

        return elements;
    }

    private List<FileResource> ExtractFiles(ZipArchive archive)
    {
        var files = new List<FileResource>();

        // /aasx/files/ 폴더의 모든 파일
        var fileEntries = archive.Entries
            .Where(e => e.FullName.StartsWith("aasx/files/"));

        foreach (var entry in fileEntries)
        {
            using var stream = entry.Open();
            using var ms = new MemoryStream();
            stream.CopyTo(ms);

            files.Add(new FileResource
            {
                Path = entry.FullName,
                ContentType = GetContentType(entry.Name),
                Encoding = "base64",
                Data = Convert.ToBase64String(ms.ToArray())
            });
        }

        return files;
    }

    private string GetContentType(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLower();
        return ext switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".pdf" => "application/pdf",
            ".xml" => "application/xml",
            _ => "application/octet-stream"
        };
    }

    private string? ParseSemanticId(XElement? semanticIdElement)
    {
        if (semanticIdElement == null) return null;

        var ns = XNamespace.Get("https://admin-shell.io/aas/3/0");
        return semanticIdElement
            .Descendants(ns + "key")
            .FirstOrDefault()?
            .Element(ns + "value")?
            .Value;
    }

    private AssetInformation ParseAssetInfo(XElement? assetInfoElement)
    {
        if (assetInfoElement == null)
            throw new InvalidDataException("AssetInformation missing");

        var ns = XNamespace.Get("https://admin-shell.io/aas/3/0");

        return new AssetInformation
        {
            AssetKind = assetInfoElement.Element(ns + "assetKind")?.Value ?? "Instance",
            GlobalAssetId = assetInfoElement.Element(ns + "globalAssetId")?.Value
        };
    }

    private List<string> ParseSubmodelRefs(XElement? submodelsElement)
    {
        if (submodelsElement == null) return new List<string>();

        var ns = XNamespace.Get("https://admin-shell.io/aas/3/0");

        return submodelsElement
            .Descendants(ns + "key")
            .Select(k => k.Element(ns + "value")?.Value)
            .Where(v => v != null)
            .ToList()!;
    }
}

// DTO Models
public class AasxJsonModel
{
    public string AasVersion { get; set; } = "3.0";
    public AasShell AssetAdministrationShell { get; set; } = new();
    public List<Submodel> Submodels { get; set; } = new();
    public List<FileResource> Files { get; set; } = new();
}

public class AasShell
{
    public string? IdShort { get; set; }
    public string? Id { get; set; }
    public AssetInformation AssetInformation { get; set; } = new();
    public List<string> SubmodelRefs { get; set; } = new();
}

public class AssetInformation
{
    public string AssetKind { get; set; } = "Instance";
    public string? GlobalAssetId { get; set; }
}

public class Submodel
{
    public string? IdShort { get; set; }
    public string? Id { get; set; }
    public string? SemanticId { get; set; }
    public string Kind { get; set; } = "Instance";
    public List<SubmodelElement> SubmodelElements { get; set; } = new();
}

public class SubmodelElement
{
    public string? IdShort { get; set; }
    public string Type { get; set; } = "Property";
    public string? SemanticId { get; set; }
    public string? ValueType { get; set; }
    public object? Value { get; set; }
}

public class FileResource
{
    public string Path { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public string Encoding { get; set; } = "base64";
    public string Data { get; set; } = string.Empty;
}
```

### 5.3 C# 구현: JSON → AASX

```csharp
public class JsonToAasxConverter
{
    public async Task ConvertAsync(string jsonPath, string outputAasxPath)
    {
        // 1. JSON 파싱
        var jsonContent = await File.ReadAllTextAsync(jsonPath);
        var model = JsonSerializer.Deserialize<AasxJsonModel>(jsonContent);

        if (model == null)
            throw new InvalidDataException("Invalid JSON");

        // 2. ZIP 아카이브 생성
        using var archive = ZipFile.Open(outputAasxPath, ZipArchiveMode.Create);

        // 3. AAS XML 생성
        var aasXml = GenerateAasXml(model.AssetAdministrationShell);
        var aasEntry = archive.CreateEntry("aasx/xml/aas.xml");
        using (var writer = new StreamWriter(aasEntry.Open()))
        {
            await writer.WriteAsync(aasXml.ToString());
        }

        // 4. Submodel XML 생성
        foreach (var submodel in model.Submodels)
        {
            var submodelXml = GenerateSubmodelXml(submodel);
            var submodelEntry = archive.CreateEntry($"submodels/{submodel.IdShort}/submodel.xml");
            using (var writer = new StreamWriter(submodelEntry.Open()))
            {
                await writer.WriteAsync(submodelXml.ToString());
            }
        }

        // 5. 파일 리소스 추가
        foreach (var file in model.Files)
        {
            var fileEntry = archive.CreateEntry(file.Path);
            using (var writer = fileEntry.Open())
            {
                var bytes = Convert.FromBase64String(file.Data);
                await writer.WriteAsync(bytes, 0, bytes.Length);
            }
        }

        // 6. [Content_Types].xml 생성
        var contentTypesXml = GenerateContentTypesXml();
        var contentTypesEntry = archive.CreateEntry("[Content_Types].xml");
        using (var writer = new StreamWriter(contentTypesEntry.Open()))
        {
            await writer.WriteAsync(contentTypesXml.ToString());
        }
    }

    private XDocument GenerateAasXml(AasShell aasShell)
    {
        var ns = XNamespace.Get("https://admin-shell.io/aas/3/0");

        return new XDocument(
            new XElement(ns + "environment",
                new XAttribute("xmlns", ns.NamespaceName),
                new XElement(ns + "assetAdministrationShells",
                    new XElement(ns + "assetAdministrationShell",
                        new XElement(ns + "idShort", aasShell.IdShort),
                        new XElement(ns + "id", aasShell.Id),
                        new XElement(ns + "assetInformation",
                            new XElement(ns + "assetKind", aasShell.AssetInformation.AssetKind),
                            new XElement(ns + "globalAssetId", aasShell.AssetInformation.GlobalAssetId)
                        ),
                        new XElement(ns + "submodels",
                            aasShell.SubmodelRefs.Select(smRef =>
                                new XElement(ns + "reference",
                                    new XElement(ns + "type", "ModelReference"),
                                    new XElement(ns + "keys",
                                        new XElement(ns + "key",
                                            new XElement(ns + "type", "Submodel"),
                                            new XElement(ns + "value", smRef)
                                        )
                                    )
                                )
                            )
                        )
                    )
                )
            )
        );
    }

    private XDocument GenerateSubmodelXml(Submodel submodel)
    {
        var ns = XNamespace.Get("https://admin-shell.io/aas/3/0");

        return new XDocument(
            new XElement(ns + "submodel",
                new XAttribute("xmlns", ns.NamespaceName),
                new XElement(ns + "idShort", submodel.IdShort),
                new XElement(ns + "id", submodel.Id),
                GenerateSemanticIdElement(ns, submodel.SemanticId),
                new XElement(ns + "kind", submodel.Kind),
                new XElement(ns + "submodelElements",
                    submodel.SubmodelElements.Select(elem => GenerateSubmodelElementXml(ns, elem))
                )
            )
        );
    }

    private XElement GenerateSubmodelElementXml(XNamespace ns, SubmodelElement element)
    {
        return element.Type switch
        {
            "Property" => new XElement(ns + "property",
                new XElement(ns + "idShort", element.IdShort),
                GenerateSemanticIdElement(ns, element.SemanticId),
                new XElement(ns + "valueType", element.ValueType),
                new XElement(ns + "value", element.Value)
            ),
            "SubmodelElementCollection" => new XElement(ns + "submodelElementCollection",
                new XElement(ns + "idShort", element.IdShort),
                new XElement(ns + "value",
                    ((List<SubmodelElement>)element.Value!).Select(child =>
                        GenerateSubmodelElementXml(ns, child)
                    )
                )
            ),
            _ => throw new NotSupportedException($"Element type {element.Type} not supported")
        };
    }

    private XElement? GenerateSemanticIdElement(XNamespace ns, string? semanticId)
    {
        if (string.IsNullOrEmpty(semanticId)) return null;

        return new XElement(ns + "semanticId",
            new XElement(ns + "type", "ExternalReference"),
            new XElement(ns + "keys",
                new XElement(ns + "key",
                    new XElement(ns + "type", "GlobalReference"),
                    new XElement(ns + "value", semanticId)
                )
            )
        );
    }

    private XDocument GenerateContentTypesXml()
    {
        var ns = XNamespace.Get("http://schemas.openxmlformats.org/package/2006/content-types");

        return new XDocument(
            new XElement(ns + "Types",
                new XAttribute("xmlns", ns.NamespaceName),
                new XElement(ns + "Default",
                    new XAttribute("Extension", "xml"),
                    new XAttribute("ContentType", "application/xml")
                ),
                new XElement(ns + "Default",
                    new XAttribute("Extension", "png"),
                    new XAttribute("ContentType", "image/png")
                ),
                new XElement(ns + "Default",
                    new XAttribute("Extension", "pdf"),
                    new XAttribute("ContentType", "application/pdf")
                )
            )
        );
    }
}
```

---

## 6. Web 기반 JSON Editor

### 6.1 React 기반 Editor UI

```tsx
// AasxJsonEditor.tsx
import React, { useState } from 'react';
import MonacoEditor from '@monaco-editor/react';
import { validateAasxJson } from './validator';

export const AasxJsonEditor: React.FC = () => {
  const [jsonContent, setJsonContent] = useState<string>('');
  const [validationErrors, setValidationErrors] = useState<string[]>([]);
  const [isValid, setIsValid] = useState<boolean>(true);

  const handleEditorChange = (value: string | undefined) => {
    if (!value) return;

    setJsonContent(value);

    // Real-time validation
    const errors = validateAasxJson(value);
    setValidationErrors(errors);
    setIsValid(errors.length === 0);
  };

  const handleDownloadAasx = async () => {
    if (!isValid) {
      alert('JSON has validation errors. Please fix them first.');
      return;
    }

    // JSON → AASX 변환 API 호출
    const response = await fetch('/api/aasx/convert/json-to-aasx', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: jsonContent
    });

    const blob = await response.blob();
    const url = window.URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = 'output.aasx';
    a.click();
  };

  return (
    <div className="aasx-editor">
      <div className="toolbar">
        <button onClick={() => window.open('/schema/aasx-schema.json')}>
          📄 View Schema
        </button>
        <button onClick={handleDownloadAasx} disabled={!isValid}>
          💾 Download AASX
        </button>
        <div className={`status ${isValid ? 'valid' : 'invalid'}`}>
          {isValid ? '✅ Valid' : '❌ Invalid'}
        </div>
      </div>

      <div className="editor-container">
        <MonacoEditor
          height="70vh"
          language="json"
          theme="vs-dark"
          value={jsonContent}
          onChange={handleEditorChange}
          options={{
            minimap: { enabled: false },
            fontSize: 14,
            lineNumbers: 'on',
            automaticLayout: true,
            tabSize: 2
          }}
        />
      </div>

      {validationErrors.length > 0 && (
        <div className="errors-panel">
          <h3>Validation Errors</h3>
          <ul>
            {validationErrors.map((error, index) => (
              <li key={index}>{error}</li>
            ))}
          </ul>
        </div>
      )}
    </div>
  );
};
```

### 6.2 JSON Schema 기반 자동 완성

```typescript
// Monaco Editor 설정
import * as monaco from 'monaco-editor';

// JSON Schema 로드
fetch('/schema/aasx-schema.json')
  .then(response => response.json())
  .then(schema => {
    monaco.languages.json.jsonDefaults.setDiagnosticsOptions({
      validate: true,
      schemas: [{
        uri: 'https://dualsoft.com/schema/aasx/v1.0.json',
        fileMatch: ['*'],
        schema: schema
      }]
    });
  });
```

### 6.3 템플릿 기능

```typescript
const templates = {
  "Digital Nameplate": {
    "idShort": "Nameplate",
    "id": "https://example.com/submodel/nameplate/1",
    "semanticId": "https://admin-shell.io/zvei/nameplate/2/0/Nameplate",
    "submodelElements": [
      {
        "idShort": "ManufacturerName",
        "type": "Property",
        "valueType": "string",
        "value": ""
      },
      {
        "idShort": "SerialNumber",
        "type": "Property",
        "valueType": "string",
        "value": ""
      }
    ]
  },
  "Technical Data": {
    "idShort": "TechnicalData",
    "id": "https://example.com/submodel/techdata/1",
    "semanticId": "https://admin-shell.io/ZVEI/TechnicalData/Submodel/1/2",
    "submodelElements": [
      {
        "idShort": "Weight",
        "type": "Property",
        "valueType": "double",
        "value": 0
      },
      {
        "idShort": "Dimensions",
        "type": "SubmodelElementCollection",
        "value": [
          {
            "idShort": "Length",
            "type": "Property",
            "valueType": "double",
            "value": 0
          },
          {
            "idShort": "Width",
            "type": "Property",
            "valueType": "double",
            "value": 0
          }
        ]
      }
    ]
  }
};

// 템플릿 삽입 함수
function insertTemplate(templateName: string) {
  const template = templates[templateName];
  const currentJson = JSON.parse(editor.getValue());

  if (!currentJson.submodels) {
    currentJson.submodels = [];
  }

  currentJson.submodels.push(template);

  editor.setValue(JSON.stringify(currentJson, null, 2));
}
```

---

## 7. CLI 도구

### 7.1 aasx2json CLI

```bash
# 기본 사용
$ aasx2json input.aasx output.json

# 옵션
$ aasx2json input.aasx output.json \
    --pretty              # 보기 좋게 포맷팅
    --embed-files         # 파일을 Base64로 임베드
    --validate            # 변환 후 검증
    --schema schema.json  # 커스텀 스키마 사용

# 배치 처리
$ aasx2json *.aasx --output-dir ./json/

# 표준 출력으로
$ aasx2json input.aasx - | jq '.submodels[0].idShort'
```

### 7.2 json2aasx CLI

```bash
# 기본 사용
$ json2aasx input.json output.aasx

# 옵션
$ json2aasx input.json output.aasx \
    --validate            # 변환 전 검증
    --schema schema.json  # 커스텀 스키마 사용
    --strict              # 엄격 모드 (오류 시 중단)

# 배치 처리
$ json2aasx *.json --output-dir ./aasx/
```

### 7.3 C# CLI 구현

```csharp
// Program.cs
using System.CommandLine;
using DSPilot.AasxTools;

var rootCommand = new RootCommand("AASX ↔ JSON Converter");

// aasx2json 명령
var aasx2jsonCommand = new Command("aasx2json", "Convert AASX to JSON");
var inputOption = new Option<FileInfo>("--input", "Input AASX file") { IsRequired = true };
var outputOption = new Option<FileInfo>("--output", "Output JSON file") { IsRequired = true };
var prettyOption = new Option<bool>("--pretty", () => true, "Pretty print JSON");
var embedFilesOption = new Option<bool>("--embed-files", () => false, "Embed files as Base64");

aasx2jsonCommand.AddOption(inputOption);
aasx2jsonCommand.AddOption(outputOption);
aasx2jsonCommand.AddOption(prettyOption);
aasx2jsonCommand.AddOption(embedFilesOption);

aasx2jsonCommand.SetHandler(async (FileInfo input, FileInfo output, bool pretty, bool embedFiles) =>
{
    Console.WriteLine($"Converting {input.Name} → {output.Name}...");

    var converter = new AasxToJsonConverter();
    var json = await converter.ConvertAsync(input.FullName);

    await File.WriteAllTextAsync(output.FullName, json);

    Console.WriteLine("✅ Conversion complete!");
}, inputOption, outputOption, prettyOption, embedFilesOption);

// json2aasx 명령
var json2aasxCommand = new Command("json2aasx", "Convert JSON to AASX");
json2aasxCommand.AddOption(inputOption);
json2aasxCommand.AddOption(outputOption);

json2aasxCommand.SetHandler(async (FileInfo input, FileInfo output) =>
{
    Console.WriteLine($"Converting {input.Name} → {output.Name}...");

    var converter = new JsonToAasxConverter();
    await converter.ConvertAsync(input.FullName, output.FullName);

    Console.WriteLine("✅ Conversion complete!");
}, inputOption, outputOption);

rootCommand.AddCommand(aasx2jsonCommand);
rootCommand.AddCommand(json2aasxCommand);

return await rootCommand.InvokeAsync(args);
```

---

## 8. 워크플로우

### 8.1 일반 사용자 워크플로우

```
1. AASX 파일 받음 (이메일/공유 폴더)
   ↓
2. CLI 또는 Web에서 AASX → JSON 변환
   $ aasx2json product.aasx product.json
   ↓
3. VS Code로 JSON 편집
   - 자동 완성 활용
   - JSON Schema 검증
   ↓
4. JSON → AASX 재변환
   $ json2aasx product.json product-modified.aasx
   ↓
5. AASX Package Explorer로 최종 확인
```

### 8.2 개발자 자동화 워크플로우

```python
# Python 스크립트로 AASX 대량 생성
import json
import subprocess

# 1. JSON 템플릿 로드
with open('template.json') as f:
    template = json.load(f)

# 2. 100개 제품 정보로 AASX 생성
for i in range(100):
    # JSON 수정
    product_json = template.copy()
    product_json['assetAdministrationShell']['id'] = f'https://example.com/aas/product-{i:03d}'
    product_json['submodels'][0]['submodelElements'][0]['value'] = f'Product-{i:03d}'

    # 임시 JSON 저장
    json_path = f'temp_product_{i:03d}.json'
    with open(json_path, 'w') as f:
        json.dump(product_json, f, indent=2)

    # JSON → AASX 변환
    aasx_path = f'output/product_{i:03d}.aasx'
    subprocess.run(['json2aasx', json_path, aasx_path])

    print(f'Generated {aasx_path}')
```

### 8.3 Git 버전 관리 워크플로우

```bash
# 1. AASX → JSON 변환 (한 번만)
$ aasx2json product.aasx product.json

# 2. Git 저장소에 JSON만 커밋
$ git add product.json
$ git commit -m "Initial AASX definition"

# 3. JSON 수정 후 Diff 확인
$ git diff product.json

# 4. 변경사항 커밋
$ git commit -am "Update manufacturer name"

# 5. 배포 시 AASX 재생성
$ json2aasx product.json product.aasx
```

---

## 9. 구현 가이드

### 9.1 필수 NuGet 패키지

```xml
<!-- AASX 처리 -->
<PackageReference Include="System.IO.Compression" Version="8.0.0" />
<PackageReference Include="System.IO.Compression.ZipFile" Version="8.0.0" />

<!-- XML 처리 -->
<PackageReference Include="System.Xml.Linq" Version="8.0.0" />

<!-- JSON 처리 -->
<PackageReference Include="System.Text.Json" Version="8.0.0" />

<!-- CLI -->
<PackageReference Include="System.CommandLine" Version="2.0.0" />

<!-- JSON Schema 검증 -->
<PackageReference Include="NJsonSchema" Version="11.0.0" />
```

### 9.2 프로젝트 구조

```
DSPilot.AasxTools/
├── Converters/
│   ├── AasxToJsonConverter.cs
│   ├── JsonToAasxConverter.cs
│   └── AasxJsonModel.cs
├── Validation/
│   ├── JsonSchemaValidator.cs
│   └── AasxConformanceChecker.cs
├── CLI/
│   └── Program.cs
└── Web/
    ├── AasxJsonEditor.tsx
    ├── Validator.ts
    └── Templates.ts
```

### 9.3 구현 우선순위 (6주)

#### Week 1: AASX → JSON Converter
- [ ] AASX ZIP 읽기
- [ ] AAS XML 파싱
- [ ] Submodel XML 파싱
- [ ] JSON 직렬화

#### Week 2: JSON → AASX Converter
- [ ] JSON 역직렬화
- [ ] AAS XML 생성
- [ ] Submodel XML 생성
- [ ] ZIP 패키징

#### Week 3: JSON Schema & Validation
- [ ] JSON Schema 정의
- [ ] Schema 기반 검증
- [ ] 오류 메시지 개선

#### Week 4: CLI 도구
- [ ] aasx2json 명령
- [ ] json2aasx 명령
- [ ] 배치 처리
- [ ] 옵션 플래그

#### Week 5: Web Editor (React)
- [ ] Monaco Editor 통합
- [ ] Real-time validation
- [ ] 템플릿 기능
- [ ] AASX 다운로드

#### Week 6: 통합 테스트 & 문서화
- [ ] 표준 AASX 파일 테스트
- [ ] 사용자 가이드
- [ ] API 문서
- [ ] 배포

---

## 10. 요약

### 핵심 가치

1. **단순화**: XML → JSON 변환으로 복잡도 90% 감소
2. **범용성**: VS Code, Python, Node.js 등 모든 도구 사용 가능
3. **Git 친화적**: 텍스트 기반으로 버전 관리 용이
4. **자동화**: 스크립트로 AASX 대량 생성 가능
5. **검증**: JSON Schema로 실시간 오류 검출

### 다음 단계

1. ✅ AASX-JSON 매핑 완료
2. ⏭️ C# Converter 구현
3. ⏭️ CLI 도구 개발
4. ⏭️ Web Editor 프로토타입
5. ⏭️ 표준 AASX 테스트

---

**문서 끝**
