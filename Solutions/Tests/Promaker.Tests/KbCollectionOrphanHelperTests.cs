using Promaker.Knowledge;
using Promaker.LlmAgent;
using Xunit;

namespace Promaker.Tests;

/// <summary>
/// **D-S7-3c (s6-r31) — KbCollectionOrphanHelper SSOT 회귀 시험**
/// (todo-lighthouse-kb-server.md §3.16.8).
/// </summary>
public sealed class KbCollectionOrphanHelperTests
{
    [Fact]
    public void Null_config_returns_empty()
    {
        // 방어 path — caller 가 null 전달 시 silent empty.
        Assert.Empty(KbCollectionOrphanHelper.FindOrphans(null!));
        Assert.Equal(0, KbCollectionOrphanHelper.CountOrphans(null!));
        Assert.Equal(0, KbCollectionOrphanHelper.CountActiveOrphans(null!));
    }

    [Fact]
    public void Empty_KbCollections_returns_empty()
    {
        var cfg = new LlmConfig();
        Assert.Empty(KbCollectionOrphanHelper.FindOrphans(cfg));
        Assert.Equal(0, KbCollectionOrphanHelper.CountOrphans(cfg));
    }

    [Fact]
    public void Collection_with_matching_serviceId_is_not_orphan()
    {
        var cfg = new LlmConfig();
        cfg.LightHouseServices.Add(new LightHouseServiceConfig
        {
            ServiceId = "svc-A", DisplayName = "A", BaseUrl = "https://a.local:8443", Active = true,
        });
        cfg.KbCollections.Add(new KbCollectionEntry { CollectionId = "c1", DisplayName = "D1", Active = true, ServiceId = "svc-A" });

        Assert.Empty(KbCollectionOrphanHelper.FindOrphans(cfg));
        Assert.Equal(0, KbCollectionOrphanHelper.CountOrphans(cfg));
    }

    [Fact]
    public void Collection_with_unknown_serviceId_is_orphan()
    {
        var cfg = new LlmConfig();
        cfg.LightHouseServices.Add(new LightHouseServiceConfig
        {
            ServiceId = "svc-A", DisplayName = "A", BaseUrl = "https://a.local:8443", Active = true,
        });
        cfg.KbCollections.Add(new KbCollectionEntry { CollectionId = "c1", DisplayName = "D1", Active = true, ServiceId = "svc-DELETED" });
        cfg.KbCollections.Add(new KbCollectionEntry { CollectionId = "c2", DisplayName = "D2", Active = false, ServiceId = "svc-A" });

        var orphans = KbCollectionOrphanHelper.FindOrphans(cfg);
        Assert.Single(orphans);
        Assert.Equal("c1", orphans[0].CollectionId);
        Assert.Equal(1, KbCollectionOrphanHelper.CountOrphans(cfg));
    }

    [Fact]
    public void Empty_serviceId_is_not_orphan_legacy_path()
    {
        // ServiceId 빈 값 = D-S7-3a migration 직전 또는 legacy. orphan 으로 간주 안 함 (migration 이 채우거나, 사용자가 명시 service 선택 의무).
        var cfg = new LlmConfig();
        cfg.LightHouseServices.Add(new LightHouseServiceConfig
        {
            ServiceId = "svc-A", DisplayName = "A", BaseUrl = "https://a.local:8443", Active = true,
        });
        cfg.KbCollections.Add(new KbCollectionEntry { CollectionId = "c1", DisplayName = "D1", Active = true, ServiceId = "" });

        Assert.Empty(KbCollectionOrphanHelper.FindOrphans(cfg));
    }

    [Fact]
    public void Inactive_service_makes_collection_active_orphan()
    {
        // CountActiveOrphans = Active=true 인 collection 중 소속 service 가 *active 가 아닌* (deactivated) 경우. ChatViewModel 진단 chip path.
        var cfg = new LlmConfig();
        cfg.LightHouseServices.Add(new LightHouseServiceConfig
        {
            ServiceId = "svc-A", DisplayName = "A", BaseUrl = "https://a.local:8443", Active = false,
        });
        cfg.KbCollections.Add(new KbCollectionEntry { CollectionId = "c1", DisplayName = "D1", Active = true, ServiceId = "svc-A" });

        // CountOrphans 는 service 존재 자체만 보므로 0 (svc-A 가 LightHouseServices 에 있음).
        Assert.Equal(0, KbCollectionOrphanHelper.CountOrphans(cfg));
        // CountActiveOrphans 는 active service 와 match 여부 — inactive 면 orphan 으로 간주 (chat 진입 시 chip).
        Assert.Equal(1, KbCollectionOrphanHelper.CountActiveOrphans(cfg));
    }

    [Fact]
    public void Inactive_collection_with_unknown_service_is_orphan_but_not_active_orphan()
    {
        // CountActiveOrphans = Active=true 한정. Active=false 인 orphan 은 chip 미표시.
        var cfg = new LlmConfig();
        cfg.KbCollections.Add(new KbCollectionEntry { CollectionId = "c1", DisplayName = "D1", Active = false, ServiceId = "svc-DELETED" });

        Assert.Equal(1, KbCollectionOrphanHelper.CountOrphans(cfg));
        Assert.Equal(0, KbCollectionOrphanHelper.CountActiveOrphans(cfg));
    }
}
