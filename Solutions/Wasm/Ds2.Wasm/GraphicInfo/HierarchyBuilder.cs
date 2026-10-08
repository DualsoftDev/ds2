// 되살린 파일 — 원본이 어느 커밋에도 없어 **컴파일된 IL 에서 디컴파일**했다.
//
// 까닭: `Ds2.Wasm.csproj` 가 이 파일을 Promaker 쪽 경로로 «링크» 해 쓰는데, 그 경로가
// 사라졌고(폴더째 없다) git 도 이 파일을 **한 번도 추적한 적이 없다**. 그 바람에
// `npm run build:wasm` 이 CS2001 로 멈췄다 — 즉 엔진을 아무도 다시 굽지 못했다.
//
// 되살린 근거: `bin/Release/net9.0/Ds2.Wasm.dll` (2026-10-04 빌드).
// 같은 입력에 **같은 JSON** 을 내는지 ds2-studio 쪽 검사로 대조했다.
// 이름·주석은 원본 그대로가 아니다. 고칠 일이 생기면 여기를 고치면 된다.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ds2.Core;
using Ds2.Core.Store;
using Microsoft.FSharp.Core;

namespace Promaker.Windows.GraphicInfo
{
	public sealed class GraphicIo
	{
		[JsonPropertyName("in")]
		public string In { get; set; } = "";

		[JsonPropertyName("out")]
		public string Out { get; set; } = "";
	}
	public sealed class GraphicNode
	{
		public string Id { get; set; } = "";

		public string Kind { get; set; } = "";

		public string Label { get; set; } = "";

		public string? ParentId { get; set; }

		public List<string> ChildIds { get; } = new List<string>();

		[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		public string? Alias { get; set; }

		[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		public string? Api { get; set; }

		[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		public GraphicIo? Io { get; set; }

		[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		public string? Role { get; set; }

		[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		public string? FlowLabel { get; set; }

		[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		public int? Seq { get; set; }

		[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		public string? SrcId { get; set; }
	}
	public sealed class GraphicBinding
	{
		public string Id { get; set; } = "";

		public string From { get; set; } = "";

		public string To { get; set; } = "";

		public string Role { get; set; } = "none";

		public List<GraphicIo> EvidenceRefs { get; } = new List<GraphicIo>();
	}
	public sealed class GraphicArrow
	{
		public string From { get; set; } = "";

		public string To { get; set; } = "";

		public string Type { get; set; } = "Start";

		[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		public string? Scope { get; set; }

		[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		public string? Work { get; set; }
	}
	public sealed class GraphicProjectMeta
	{
		public string Name { get; set; } = "";

		public string Version { get; set; } = "";

		public string Author { get; set; } = "";
	}
	public sealed class GraphicMeta
	{
		public bool Cyclic { get; set; }

		public bool Static { get; set; }

		public string ApiDef { get; set; } = "";

		public GraphicProjectMeta Project { get; set; } = new GraphicProjectMeta();

		public string FlowName { get; set; } = "";

		public string LevelKind { get; set; } = "";

		public string NodeId { get; set; } = "";

		public Dictionary<string, int> Counts { get; set; } = new Dictionary<string, int>();
	}
	public sealed class GraphicHierarchy
	{
		public string RootId { get; set; } = "";

		public List<GraphicNode> Nodes { get; } = new List<GraphicNode>();

		public List<GraphicBinding> Bindings { get; } = new List<GraphicBinding>();

		public List<object> Edges { get; } = new List<object>();

		public List<GraphicArrow> WorkArrows { get; } = new List<GraphicArrow>();

		public List<GraphicArrow> CallArrows { get; } = new List<GraphicArrow>();

		public List<GraphicArrow> CallEdges { get; } = new List<GraphicArrow>();

		public List<GraphicArrow> ResetArrows { get; } = new List<GraphicArrow>();

		public GraphicMeta Meta { get; set; } = new GraphicMeta();
	}
	public static class HierarchyBuilder
	{
		private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
		{
			PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
			DefaultIgnoreCondition = JsonIgnoreCondition.Never,
			Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
		};

		public static string WorkNodeId(Guid workId)
		{
			return "W:" + workId;
		}

		public static string CallNodeId(Guid callId)
		{
			return "C:" + callId;
		}

		public static string BuildJson(DsStore store, Guid projectId)
		{
			return JsonSerializer.Serialize(Build(store, projectId), JsonOpts);
		}

		public static GraphicHierarchy Build(DsStore store, Guid projectId)
		{
			GraphicHierarchy h = new GraphicHierarchy();
			Dictionary<string, GraphicNode> byId = new Dictionary<string, GraphicNode>();
			Project project = Queries.getProject(projectId, store)?.Value;
			string text = "S:" + projectId;
			string text2 = (string.IsNullOrWhiteSpace(project?.Name) ? "Project" : project.Name);
			GraphicNode graphicNode = Add(new GraphicNode
			{
				Id = text,
				Kind = "System",
				Label = text2,
				ParentId = null
			});
			h.RootId = text;
			List<DsSystem> list = Queries.activeSystemsOf(projectId, store).ToList();
			if (list.Count == 0)
			{
				list = store.Systems.Values.Where((DsSystem s) => store.Flows.Values.Any((Flow f) => f.ParentId == s.Id)).ToList();
			}
			bool flag = list.Count > 1;
			HashSet<Guid> hashSet = new HashSet<Guid>();
			Dictionary<string, List<string>> dictionary = new Dictionary<string, List<string>>();
			Dictionary<Guid, string> dictionary2 = new Dictionary<Guid, string>();
			int num = 0;
			int num2 = 0;
			List<string> value;
			string key;
			foreach (DsSystem item3 in list)
			{
				foreach (Flow item4 in (IEnumerable<Flow>)Queries.flowsOf(item3.Id, store))
				{
					string text3 = "F:" + item4.Id;
					string text4 = (flag ? (item3.Name + "." + item4.Name) : item4.Name);
					GraphicNode graphicNode2 = Add(new GraphicNode
					{
						Id = text3,
						Kind = "Flow",
						Label = text4,
						ParentId = text
					});
					graphicNode.ChildIds.Add(text3);
					value = (dictionary[text3] = new List<string>());
					List<string> list3 = value;
					int num3 = 0;
					foreach (Work item5 in (IEnumerable<Work>)Queries.worksOf(item4.Id, store))
					{
						string text5 = WorkNodeId(item5.Id);
						GraphicNode graphicNode3 = Add(new GraphicNode
						{
							Id = text5,
							Kind = "Work",
							Label = (string.IsNullOrWhiteSpace(item5.LocalName) ? item5.Name : item5.LocalName),
							ParentId = text3,
							FlowLabel = text4,
							Seq = num3++,
							SrcId = item5.Id.ToString()
						});
						graphicNode2.ChildIds.Add(text5);
						list3.Add(text5);
						hashSet.Add(item5.Id);
						Dictionary<string, string> dictionary3 = new Dictionary<string, string>(StringComparer.Ordinal);
						foreach (Call item6 in (IEnumerable<Call>)Queries.callsOf(item5.Id, store))
						{
							string text6 = CallNodeId(item6.Id);
							(string In, string Out) tuple = ResolveIo(store, item6);
							string item = tuple.In;
							string item2 = tuple.Out;
							bool flag2 = item2.Length > 0;
							bool flag3 = item.Length > 0;
							key = (flag2 ? ((!flag3) ? "out" : "mixed") : ((!flag3) ? "none" : "in"));
							string text7 = key;
							GraphicIo graphicIo = new GraphicIo
							{
								In = item,
								Out = item2
							};
							string text8 = item6.DevicesAlias ?? "";
							string text9 = item6.ApiName ?? "";
							string label = (string.IsNullOrWhiteSpace(item6.Name) ? (text8 + "." + text9) : item6.Name);
							Add(new GraphicNode
							{
								Id = text6,
								Kind = "Call",
								Label = label,
								ParentId = text5,
								Alias = text8,
								Api = text9,
								Io = graphicIo,
								Role = text7
							});
							graphicNode3.ChildIds.Add(text6);
							num++;
							if (text7 != "none")
							{
								num2++;
							}
							var (text10, label2) = ResolveDevice(store, item6);
							if (!dictionary3.TryGetValue(text10, out var value2))
							{
								value2 = (dictionary3[text10] = $"D:{item5.Id}:{text10}");
								Add(new GraphicNode
								{
									Id = value2,
									Kind = "Device",
									Label = label2,
									ParentId = text5,
									Alias = text8,
									Api = text9,
									Io = graphicIo,
									Role = text7
								});
							}
							dictionary2[item6.Id] = value2;
							GraphicBinding graphicBinding = new GraphicBinding
							{
								Id = "b:" + text6,
								From = text6,
								To = value2,
								Role = text7
							};
							if (text7 != "none")
							{
								graphicBinding.EvidenceRefs.Add(graphicIo);
							}
							h.Bindings.Add(graphicBinding);
						}
						foreach (ArrowBetweenCalls item7 in (IEnumerable<ArrowBetweenCalls>)Queries.arrowCallsOf(item5.Id, store))
						{
							h.CallEdges.Add(new GraphicArrow
							{
								From = CallNodeId(item7.SourceId),
								To = CallNodeId(item7.TargetId),
								Type = item7.ArrowType.ToString(),
								Work = text5
							});
							if (dictionary2.TryGetValue(item7.SourceId, out var value3) && dictionary2.TryGetValue(item7.TargetId, out var value4) && !(value3 == value4))
							{
								string type = item7.ArrowType.ToString();
								if (IsReset(type))
								{
									h.ResetArrows.Add(new GraphicArrow
									{
										From = value3,
										To = value4,
										Type = type,
										Scope = "call",
										Work = text5
									});
								}
								else
								{
									h.CallArrows.Add(new GraphicArrow
									{
										From = value3,
										To = value4,
										Type = type,
										Work = text5
									});
								}
							}
						}
					}
				}
				foreach (ArrowBetweenWorks item8 in (IEnumerable<ArrowBetweenWorks>)Queries.arrowWorksOf(item3.Id, store))
				{
					if (!hashSet.Contains(item8.SourceId) || !hashSet.Contains(item8.TargetId))
					{
						continue;
					}
					string text12 = WorkNodeId(item8.SourceId);
					string text13 = WorkNodeId(item8.TargetId);
					if (!(text12 == text13))
					{
						string type2 = item8.ArrowType.ToString();
						if (IsReset(type2))
						{
							h.ResetArrows.Add(new GraphicArrow
							{
								From = text12,
								To = text13,
								Type = type2,
								Scope = "work"
							});
						}
						else
						{
							h.WorkArrows.Add(new GraphicArrow
							{
								From = text12,
								To = text13,
								Type = type2
							});
						}
					}
				}
			}
			HashSet<string> hashSet2 = new HashSet<string>(h.WorkArrows.Select((GraphicArrow a) => a.From).Concat(h.WorkArrows.Select((GraphicArrow a) => a.To)).Concat(h.ResetArrows.Where((GraphicArrow a) => a.Scope == "work").SelectMany((GraphicArrow a) => new string[2] { a.From, a.To })));
			foreach (KeyValuePair<string, List<string>> item9 in dictionary)
			{
				item9.Deconstruct(out key, out value);
				List<string> list4 = value;
				if (list4.Count >= 2 && !list4.Any(hashSet2.Contains))
				{
					for (int i = 0; i < list4.Count - 1; i++)
					{
						h.WorkArrows.Add(new GraphicArrow
						{
							From = list4[i],
							To = list4[i + 1],
							Type = "Start"
						});
					}
				}
			}
			int num4 = h.Nodes.Count((GraphicNode n) => n.Kind == "Flow");
			int value5 = h.Nodes.Count((GraphicNode n) => n.Kind == "Work");
			int value6 = h.Nodes.Count((GraphicNode n) => n.Kind == "Device");
			h.Meta = new GraphicMeta
			{
				Cyclic = IsCyclic(h.WorkArrows),
				Static = false,
				ApiDef = text2,
				Project = new GraphicProjectMeta
				{
					Name = (project?.Name ?? ""),
					Version = (project?.Version ?? ""),
					Author = (project?.Author ?? "")
				},
				FlowName = ((num4 == 1) ? h.Nodes.First((GraphicNode n) => n.Kind == "Flow").Label : text2),
				LevelKind = "Project",
				NodeId = projectId.ToString(),
				Counts = new Dictionary<string, int>
				{
					["flows"] = num4,
					["works"] = value5,
					["calls"] = num,
					["devices"] = value6,
					["io"] = num2,
					["workArrows"] = h.WorkArrows.Count,
					["callArrows"] = h.CallArrows.Count,
					["callEdges"] = h.CallEdges.Count,
					["resetArrows"] = h.ResetArrows.Count
				}
			};
			return h;
			GraphicNode Add(GraphicNode n)
			{
				if (byId.TryGetValue(n.Id, out GraphicNode value7))
				{
					return value7;
				}
				h.Nodes.Add(n);
				byId[n.Id] = n;
				return n;
			}
		}

		private static bool IsReset(string type)
		{
			return type.Contains("Reset", StringComparison.OrdinalIgnoreCase);
		}

		private static (string In, string Out) ResolveIo(DsStore store, Call call)
		{
			List<string> list = new List<string>();
			List<string> list2 = new List<string>();
			foreach (ApiCall apiCall in call.ApiCalls)
			{
				string text = TagText(apiCall.InTag);
				if (text.Length > 0 && !list.Contains(text))
				{
					list.Add(text);
				}
				string text2 = TagText(apiCall.OutTag);
				if (text2.Length > 0 && !list2.Contains(text2))
				{
					list2.Add(text2);
				}
			}
			return (In: string.Join(", ", list), Out: string.Join(", ", list2));
		}

		private static string TagText(FSharpOption<IOTag>? tag)
		{
			if (tag == null)
			{
				return "";
			}
			IOTag value = tag.Value;
			if (value == null)
			{
				return "";
			}
			string text = (value.Address ?? "").Trim();
			if (text.Length <= 0)
			{
				return (value.Name ?? "").Trim();
			}
			return text;
		}

		private static (string Key, string Label) ResolveDevice(DsStore store, Call call)
		{
			foreach (ApiCall apiCall in call.ApiCalls)
			{
				FSharpOption<Guid> apiDefId = apiCall.ApiDefId;
				if (apiDefId != null && store.ApiDefs.TryGetValue(apiDefId.Value, out var value) && store.Systems.TryGetValue(value.ParentId, out var value2))
				{
					return (Key: value.ParentId.ToString(), Label: value2.Name);
				}
			}
			string text = call.DevicesAlias;
			if (string.IsNullOrWhiteSpace(text))
			{
				text = call.Name;
			}
			if (string.IsNullOrWhiteSpace(text))
			{
				text = "device";
			}
			return (Key: "alias:" + text, Label: text);
		}

		private static bool IsCyclic(List<GraphicArrow> workArrows)
		{
			if (workArrows.Count == 0)
			{
				return false;
			}
			Dictionary<string, List<string>> incoming = (from a in workArrows
				group a by a.To).ToDictionary((IGrouping<string, GraphicArrow> g) => g.Key, (IGrouping<string, GraphicArrow> g) => g.Select((GraphicArrow a) => a.From).ToList());
			Dictionary<string, int> rank = new Dictionary<string, int>();
			List<string> list = workArrows.SelectMany((GraphicArrow a) => new string[2] { a.From, a.To }).Distinct().ToList();
			foreach (string item in list)
			{
				Rank(item, new HashSet<string>());
			}
			return workArrows.Any((GraphicArrow a) => rank.GetValueOrDefault(a.From) >= rank.GetValueOrDefault(a.To));
			int Rank(string id, HashSet<string> seen)
			{
				if (rank.TryGetValue(id, out var value))
				{
					return value;
				}
				if (!seen.Add(id))
				{
					return 0;
				}
				int num = 0;
				if (incoming.TryGetValue(id, out var value2))
				{
					foreach (string item2 in value2)
					{
						num = Math.Max(num, Rank(item2, seen) + 1);
					}
				}
				seen.Remove(id);
				rank[id] = num;
				return num;
			}
		}
	}
}
