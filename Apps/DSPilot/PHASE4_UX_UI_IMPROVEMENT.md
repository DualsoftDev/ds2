# Phase 4: UX/UI 개선

**우선순위**: 중  
**예상 기간**: 3-4일  
**목표**: 사용자 경험 전면 개선 (다크모드, 반응형, 알림 시스템)

---

## 📋 작업 개요

다크모드 지원, 반응형 디자인, 네비게이션 재구성, 알림 시스템을 구현하여 현대적이고 사용하기 편한 인터페이스를 제공합니다.

---

## 🎯 작업 항목

### 1. 다크모드 구현

#### 1.1. 테마 시스템 구축
**새 파일**: `wwwroot/css/theme-dark.css`

```css
[data-theme="dark"] {
    /* 기본 색상 */
    --color-background: #1E1E1E;
    --color-surface: #2D2D2D;
    --color-card-bg: #252525;
    --color-text-primary: #E0E0E0;
    --color-text-secondary: #B0B0B0;
    --color-text-tertiary: #808080;
    
    /* Primary 색상 */
    --color-primary: #64B5F6;
    --color-primary-rgb: 100, 181, 246;
    
    /* 상태 색상 */
    --color-success: #66BB6A;
    --color-warning: #FFA726;
    --color-error: #EF5350;
    
    /* 라인 및 테두리 */
    --color-lines: #404040;
    --color-drawer-bg: #252525;
    --color-drawer-text: #E0E0E0;
    
    /* 그림자 */
    --shadow-1: 0 2px 8px rgba(0,0,0,0.3);
    --shadow-2: 0 4px 16px rgba(0,0,0,0.4);
}
```

#### 1.2. 테마 토글 서비스
**새 파일**: `DSPilot/Services/ThemeService.cs`

```csharp
namespace DSPilot.Services;

public class ThemeService
{
    private string _currentTheme = "light";

    public event Action? OnThemeChanged;

    public string CurrentTheme => _currentTheme;

    public void ToggleTheme()
    {
        _currentTheme = _currentTheme == "light" ? "dark" : "light";
        OnThemeChanged?.Invoke();
    }

    public void SetTheme(string theme)
    {
        if (_currentTheme != theme)
        {
            _currentTheme = theme;
            OnThemeChanged?.Invoke();
        }
    }
}
```

**Program.cs 등록**:
```csharp
builder.Services.AddSingleton<ThemeService>();
```

#### 1.3. 테마 토글 버튼
**파일**: `Components/Layout/MainLayout.razor`

```razor
@inject ThemeService ThemeService
@inject IJSRuntime JS
@implements IDisposable

<div data-theme="@ThemeService.CurrentTheme">
    <div class="page">
        <aside class="sidebar">
            <div class="sidebar-brand">
                <span class="material-icons">factory</span>
                <span class="brand-text">DSPilot</span>
            </div>

            <div class="sidebar-content">
                <NavMenu />
            </div>

            @* 테마 토글 버튼 *@
            <div class="sidebar-footer">
                <button class="theme-toggle-btn" @onclick="ToggleTheme">
                    <span class="material-icons">
                        @(ThemeService.CurrentTheme == "light" ? "dark_mode" : "light_mode")
                    </span>
                    <span>@(ThemeService.CurrentTheme == "light" ? "다크모드" : "라이트모드")</span>
                </button>
            </div>
        </aside>

        <main class="main-content">
            @Body
        </main>
    </div>
</div>

@code {
    protected override void OnInitialized()
    {
        ThemeService.OnThemeChanged += StateHasChanged;
    }

    private async Task ToggleTheme()
    {
        ThemeService.ToggleTheme();
        await JS.InvokeVoidAsync("localStorage.setItem", "theme", ThemeService.CurrentTheme);
    }

    public void Dispose()
    {
        ThemeService.OnThemeChanged -= StateHasChanged;
    }
}
```

### 2. 반응형 디자인

**파일**: `wwwroot/css/responsive.css` (새 파일)

```css
/* 태블릿 (768px 이하) */
@media (max-width: 768px) {
    .sidebar {
        position: fixed;
        left: -280px;
        transition: left 0.3s;
        z-index: 1000;
    }

    .sidebar.open {
        left: 0;
    }

    .main-content {
        margin-left: 0;
    }

    .dashboard-grid {
        grid-template-columns: 1fr;
    }

    .flow-workspace-kpi-grid {
        grid-template-columns: repeat(2, 1fr);
    }

    .direction-grid {
        grid-template-columns: 1fr;
    }
}

/* 모바일 (480px 이하) */
@media (max-width: 480px) {
    .typo-h5 {
        font-size: 18px;
    }

    .card {
        padding: 12px;
    }

    .flow-workspace-kpi-grid {
        grid-template-columns: 1fr;
    }

    .state-change-item {
        grid-template-columns: 80px 1fr;
        font-size: 12px;
    }

    .nav-link {
        font-size: 13px;
        padding: 10px 12px;
    }
}

/* 대형 화면 (1920px 이상) */
@media (min-width: 1920px) {
    .dashboard-grid {
        grid-template-columns: repeat(4, 1fr);
    }

    .flow-workspace-kpi-grid {
        grid-template-columns: repeat(6, 1fr);
    }
}
```

### 3. 알림 시스템

**새 파일**: `DSPilot/Services/NotificationService.cs`

```csharp
using System.Collections.Concurrent;

namespace DSPilot.Services;

public class NotificationService
{
    private readonly ConcurrentQueue<Notification> _notifications = new();
    private const int MaxNotifications = 50;

    public event Action? OnNotificationAdded;

    public void ShowNotification(string message, NotificationType type = NotificationType.Info, int durationMs = 5000)
    {
        var notification = new Notification
        {
            Id = Guid.NewGuid(),
            Message = message,
            Type = type,
            Timestamp = DateTime.Now,
            DurationMs = durationMs
        };

        _notifications.Enqueue(notification);

        // 최대 개수 초과 시 제거
        while (_notifications.Count > MaxNotifications)
        {
            _notifications.TryDequeue(out _);
        }

        OnNotificationAdded?.Invoke();
    }

    public void ShowSuccess(string message) => ShowNotification(message, NotificationType.Success);
    public void ShowWarning(string message) => ShowNotification(message, NotificationType.Warning);
    public void ShowError(string message) => ShowNotification(message, NotificationType.Error);
    public void ShowInfo(string message) => ShowNotification(message, NotificationType.Info);

    public IEnumerable<Notification> GetNotifications() => _notifications.ToArray();
}

public class Notification
{
    public Guid Id { get; set; }
    public string Message { get; set; } = "";
    public NotificationType Type { get; set; }
    public DateTime Timestamp { get; set; }
    public int DurationMs { get; set; }
}

public enum NotificationType
{
    Info,
    Success,
    Warning,
    Error
}
```

**Program.cs 등록**:
```csharp
builder.Services.AddSingleton<NotificationService>();
```

#### 3.2. 알림 컴포넌트
**새 파일**: `Components/Shared/NotificationToast.razor`

```razor
@inject NotificationService NotificationService
@implements IDisposable

<div class="notification-container">
    @foreach (var notification in _notifications.Reverse())
    {
        <div class="notification notification-@notification.Type.ToString().ToLower()"
             @key="notification.Id"
             style="animation: slideIn 0.3s ease;">
            <span class="material-icons notification-icon">
                @GetNotificationIcon(notification.Type)
            </span>
            <div class="notification-content">
                <div class="notification-message">@notification.Message</div>
                <div class="notification-time">@notification.Timestamp.ToString("HH:mm:ss")</div>
            </div>
            <button class="notification-close" @onclick="() => RemoveNotification(notification.Id)">
                <span class="material-icons">close</span>
            </button>
        </div>
    }
</div>

@code {
    private List<Notification> _notifications = new();

    protected override void OnInitialized()
    {
        NotificationService.OnNotificationAdded += OnNotificationAdded;
    }

    private void OnNotificationAdded()
    {
        _notifications = NotificationService.GetNotifications().ToList();
        InvokeAsync(StateHasChanged);

        // 자동 제거 타이머
        foreach (var notification in _notifications)
        {
            var id = notification.Id;
            var duration = notification.DurationMs;
            _ = Task.Delay(duration).ContinueWith(_ => RemoveNotification(id));
        }
    }

    private void RemoveNotification(Guid id)
    {
        _notifications.RemoveAll(n => n.Id == id);
        InvokeAsync(StateHasChanged);
    }

    private string GetNotificationIcon(NotificationType type) => type switch
    {
        NotificationType.Success => "check_circle",
        NotificationType.Warning => "warning",
        NotificationType.Error => "error",
        _ => "info"
    };

    public void Dispose()
    {
        NotificationService.OnNotificationAdded -= OnNotificationAdded;
    }
}
```

**MainLayout.razor에 추가**:
```razor
<NotificationToast />
```

---

## 📝 구현 체크리스트

- [ ] 다크모드 CSS 테마 작성
- [ ] ThemeService 구현
- [ ] 테마 토글 버튼 추가
- [ ] LocalStorage 테마 저장
- [ ] 반응형 CSS 작성
- [ ] NotificationService 구현
- [ ] NotificationToast 컴포넌트 작성
- [ ] 모바일 테스트
- [ ] 태블릿 테스트
- [ ] 다크모드 테스트

---

## 🚀 다음 단계

Phase 4 완료 후:
- `PHASE5_OPTIMIZATION.md` 진행
- 코드 정리 및 성능 최적화
