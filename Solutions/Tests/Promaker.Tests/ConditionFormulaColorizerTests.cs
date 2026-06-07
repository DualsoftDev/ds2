using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Documents;
using Ds2.Core;
using Ds2.Editor;
using Microsoft.FSharp.Collections;
using Promaker.Controls;
using Promaker.ViewModels;
using Xunit;

namespace Promaker.Tests;

// =============================================================================
// FormulaColorizer 표시 규약 일치 검증 (Phase 7 / todo-refactor-condition.md §Phase7).
//
// C# FormulaColorizer.BuildInlines 가 생성하는 inline 들의 *이어붙인 텍스트* 가
// F# ConditionFormulaProjection (= ConditionItem.FormulaText) 와 정확히 일치해야 한다.
//   - IsInverted -> `not (...)`
//   - ContactKind: NcContact `/`, RisingPulse `(R)`, FallingPulse `(F)`, Inverter `*`
//   - op join 공백: ` & ` / ` | `
//   - 빈 condition: 빈 And=`true`, 빈 Or=`false`
//   - 부모 op 항등원인 빈 자식 생략
// (inline 생성은 STA 스레드 필요 — StaTestRunner 사용.)
// =============================================================================
public sealed class ConditionFormulaColorizerTests
{
    /// leaf 패널 항목 — projection/colorizer 표시만 검증하므로 store 없이 직접 구성.
    /// condition leaf 기대값은 InputSpec(Runtime 평가 대상)이므로 기대값은 inputSpec 인자(InputSpecText)에 채운다.
    private static ConditionApiCallItem Leaf(string name, ContactKind kind, string inputSpec = "") =>
        new(Guid.NewGuid(), name, name,
            "", 0,            // outputSpec — condition leaf 표시에 쓰지 않음
            inputSpec, 0,     // inputSpec — 기대값(=spec)
            kind, ValueSpec.UndefinedValue);

    private static ConditionPanelItem Cond(
        bool isOR, bool isInverted,
        IEnumerable<ConditionApiCallItem> items,
        IEnumerable<ConditionPanelItem> children) =>
        new(Guid.NewGuid(), ConditionType.AutoAux, isOR, isInverted,
            ListModule.OfSeq(items),
            ListModule.OfSeq(children));

    private static ConditionPanelItem Cond(bool isOR, bool isInverted, params ConditionApiCallItem[] items) =>
        Cond(isOR, isInverted, items, Array.Empty<ConditionPanelItem>());

    /// colorizer 가 생성한 inline 들의 텍스트를 이어붙인다 (Hyperlink 내부 Run 포함).
    private static string ColorizedText(ConditionPanelItem panel)
    {
        string result = string.Empty;
        StaTestRunner.Run(() =>
        {
            var item = new ConditionItem(Guid.NewGuid(), panel);
            var block = new System.Windows.Controls.TextBlock();
            FormulaColorizer.BuildInlines(item, block.Inlines, navigateCommand: null);
            result = FlattenInlines(block.Inlines);
        });
        return result;
    }

    private static string FlattenInlines(InlineCollection inlines)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var inline in inlines)
            AppendInline(sb, inline);
        return sb.ToString();
    }

    private static void AppendInline(System.Text.StringBuilder sb, Inline inline)
    {
        switch (inline)
        {
            case Run run:
                sb.Append(run.Text);
                break;
            case Hyperlink link:
                foreach (var child in link.Inlines)
                    AppendInline(sb, child);
                break;
            case Span span:
                foreach (var child in span.Inlines)
                    AppendInline(sb, child);
                break;
        }
    }

    /// colorizer 텍스트와 F# projection(FormulaText)이 동일한지 — 표시 규약 일치의 핵심 불변식.
    private static void AssertMatchesProjection(ConditionPanelItem panel)
    {
        var expected = panel.FormulaText(); // F# ConditionFormulaProjection.formatCondition
        var actual = ColorizedText(panel);
        Assert.Equal(expected, actual);
    }

    // ── IsInverted ──

    [Fact]
    public void Inverted_or_is_wrapped_with_not()
    {
        var c = Cond(isOR: true, isInverted: true, Leaf("A", ContactKind.NoContact), Leaf("B", ContactKind.NoContact));
        Assert.Equal("not (A | B)", ColorizedText(c));
        AssertMatchesProjection(c);
    }

    [Fact]
    public void Non_inverted_has_no_not()
    {
        var c = Cond(isOR: true, isInverted: false, Leaf("A", ContactKind.NoContact), Leaf("B", ContactKind.NoContact));
        Assert.Equal("A | B", ColorizedText(c));
        AssertMatchesProjection(c);
    }

    [Fact]
    public void Nested_child_inverted_is_wrapped()
    {
        // A & (not (B | C))
        var child = Cond(isOR: true, isInverted: true, Leaf("B", ContactKind.NoContact), Leaf("C", ContactKind.NoContact));
        var c = Cond(isOR: false, isInverted: false,
            new[] { Leaf("A", ContactKind.NoContact) }, new[] { child });
        Assert.Equal("A & (not (B | C))", ColorizedText(c));
        AssertMatchesProjection(c);
    }

    // ── ContactKind ──

    [Fact]
    public void NcContact_prefixes_slash()
    {
        var c = Cond(isOR: false, isInverted: false, Leaf("A", ContactKind.NcContact));
        Assert.Equal("/A", ColorizedText(c));
        AssertMatchesProjection(c);
    }

    [Fact]
    public void RisingPulse_suffixes_R()
    {
        var c = Cond(isOR: false, isInverted: false, Leaf("A", ContactKind.RisingPulse));
        Assert.Equal("A(R)", ColorizedText(c));
        AssertMatchesProjection(c);
    }

    [Fact]
    public void FallingPulse_suffixes_F()
    {
        var c = Cond(isOR: false, isInverted: false, Leaf("A", ContactKind.FallingPulse));
        Assert.Equal("A(F)", ColorizedText(c));
        AssertMatchesProjection(c);
    }

    [Fact]
    public void NoContact_shows_name_only()
    {
        var c = Cond(isOR: false, isInverted: false, Leaf("A", ContactKind.NoContact));
        Assert.Equal("A", ColorizedText(c));
        AssertMatchesProjection(c);
    }

    [Fact]
    public void Five_contact_kinds_are_distinguished()
    {
        var c = Cond(isOR: false, isInverted: false,
            Leaf("A", ContactKind.NoContact),
            Leaf("B", ContactKind.NcContact),
            Leaf("C", ContactKind.RisingPulse),
            Leaf("D", ContactKind.FallingPulse),
            Leaf("E", ContactKind.Inverter));
        // Inverter 는 placeholder leaf -> `*`
        Assert.Equal("A & /B & C(R) & D(F) & *", ColorizedText(c));
        AssertMatchesProjection(c);
    }

    [Fact]
    public void ContactKind_marker_preserved_with_expected_value()
    {
        // RisingPulse + inputSpec(기대값) -> name=spec(R)
        var c = Cond(isOR: false, isInverted: false, Leaf("A", ContactKind.RisingPulse, inputSpec: "true"));
        Assert.Equal("A=true(R)", ColorizedText(c));
        AssertMatchesProjection(c);
    }

    // ── 빈 condition (Runtime 의미) ──

    [Fact]
    public void Empty_and_shows_true()
    {
        var c = Cond(isOR: false, isInverted: false);
        Assert.Equal("true", ColorizedText(c));
        AssertMatchesProjection(c);
    }

    [Fact]
    public void Empty_or_shows_false()
    {
        var c = Cond(isOR: true, isInverted: false);
        Assert.Equal("false", ColorizedText(c));
        AssertMatchesProjection(c);
    }

    [Fact]
    public void Empty_and_child_is_dropped_in_parent_and()
    {
        // A & (빈 And=true) -> A 만 (true 는 And 항등원).
        var emptyChild = Cond(isOR: false, isInverted: false);
        var c = Cond(isOR: false, isInverted: false,
            new[] { Leaf("A", ContactKind.NoContact) }, new[] { emptyChild });
        Assert.Equal("A", ColorizedText(c));
        AssertMatchesProjection(c);
    }

    [Fact]
    public void Empty_or_child_is_preserved_in_parent_and()
    {
        // A & (빈 Or=false) -> A & (false). false 는 And 항등원이 아니라 의미 보존.
        var emptyOrChild = Cond(isOR: true, isInverted: false);
        var c = Cond(isOR: false, isInverted: false,
            new[] { Leaf("A", ContactKind.NoContact) }, new[] { emptyOrChild });
        Assert.Equal("A & (false)", ColorizedText(c));
        AssertMatchesProjection(c);
    }

    // ── 회귀: 중첩/연산자/기대값 ──

    [Fact]
    public void Nested_group_is_preserved()
    {
        var child = Cond(isOR: true, isInverted: false, Leaf("B", ContactKind.NoContact), Leaf("C", ContactKind.NoContact));
        var c = Cond(isOR: false, isInverted: false,
            new[] { Leaf("A", ContactKind.NoContact) }, new[] { child });
        Assert.Equal("A & (B | C)", ColorizedText(c));
        AssertMatchesProjection(c);
    }

    [Fact]
    public void Expected_value_shows_name_eq_spec()
    {
        var c = Cond(isOR: false, isInverted: false, Leaf("A", ContactKind.NoContact, inputSpec: "true"));
        Assert.Equal("A=true", ColorizedText(c));
        AssertMatchesProjection(c);
    }

    // ── eq 기대값(InputSpec) 표시 — condition leaf 기대값 = InputSpec 확정 ──
    // (7-reviewer Major: condition leaf 기대값은 InputSpec(Phase 2 의 eq 저장 위치, Runtime 평가 대상)이며
    //  colorizer 가 `=spec` 으로 표시해야 한다. OutputSpec 은 condition leaf 표시에 쓰지 않는다.)

    /// InputSpec(ValueSpec) 으로부터 leaf 를 만든다 — Panel.fs 생성부와 동일 규칙으로
    /// InputSpecText 를 채운다. PropertyPanelValueSpec.format 은 ValueSpecText.format(공용 SSOT)
    /// 위임이라 결과 동일하고, typeIndex 는 표시 검증에 무관하므로 0 고정.
    private static ConditionApiCallItem LeafOfInputSpec(string name, ContactKind kind, ValueSpec inputSpec) =>
        new(Guid.NewGuid(), name, name,
            "", 0,
            ValueSpecText.format(inputSpec), 0,
            kind, inputSpec);

    [Fact]
    public void InputSpec_bool_true_shows_eq_true()
    {
        var c = Cond(isOR: false, isInverted: false,
            LeafOfInputSpec("A", ContactKind.NoContact, ValueSpecModule.singleBool(true)));
        Assert.Equal("A=true", ColorizedText(c));
        AssertMatchesProjection(c);
    }

    [Fact]
    public void InputSpec_string_OPEN_shows_eq_OPEN()
    {
        var c = Cond(isOR: false, isInverted: false,
            LeafOfInputSpec("Door", ContactKind.NoContact, ValueSpecModule.singleString("OPEN")));
        Assert.Equal("Door=OPEN", ColorizedText(c));
        AssertMatchesProjection(c);
    }

    [Fact]
    public void InputSpec_numeric_shows_eq_value()
    {
        var c = Cond(isOR: false, isInverted: false,
            LeafOfInputSpec("Cnt", ContactKind.NoContact, ValueSpecModule.singleInt32(42)));
        Assert.Equal("Cnt=42", ColorizedText(c));
        AssertMatchesProjection(c);
    }

    [Fact]
    public void InputSpec_expected_value_combines_with_contact_kind()
    {
        // A접 -> name=spec, B접(NcContact) -> /name=spec, Rising/Falling -> name=spec(R)/(F)
        Assert.Equal("A=true",
            ColorizedText(Cond(isOR: false, isInverted: false,
                LeafOfInputSpec("A", ContactKind.NoContact, ValueSpecModule.singleBool(true)))));
        Assert.Equal("/A=true",
            ColorizedText(Cond(isOR: false, isInverted: false,
                LeafOfInputSpec("A", ContactKind.NcContact, ValueSpecModule.singleBool(true)))));
        Assert.Equal("A=true(R)",
            ColorizedText(Cond(isOR: false, isInverted: false,
                LeafOfInputSpec("A", ContactKind.RisingPulse, ValueSpecModule.singleBool(true)))));
        Assert.Equal("A=true(F)",
            ColorizedText(Cond(isOR: false, isInverted: false,
                LeafOfInputSpec("A", ContactKind.FallingPulse, ValueSpecModule.singleBool(true)))));
    }

    [Fact]
    public void OutputSpec_only_does_not_show_expected_value()
    {
        // condition leaf 표시는 InputSpec 기준 — OutputSpec 에 값이 있어도 InputSpec 이 비면 name 만.
        var item = new ConditionApiCallItem(
            Guid.NewGuid(), "A", "A",
            "true", 0,     // outputSpec — 표시에 쓰지 않음
            "", 0,         // inputSpec 비어 있음
            ContactKind.NoContact, ValueSpec.UndefinedValue);
        var c = Cond(isOR: false, isInverted: false, item);
        Assert.Equal("A", ColorizedText(c));
        AssertMatchesProjection(c);
    }
}
