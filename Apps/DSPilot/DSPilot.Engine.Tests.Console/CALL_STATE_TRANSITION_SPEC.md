# Call 상태 전이 스펙

## 1. In / Out 신호 정의

| 신호 | 의미 | 역할 |
|------|------|------|
| **Out** | 외부에서 Call로 들어오는 신호 (활성화) | Going 진입 트리거 |
| **In** | Call이 처리 후 내보내는 신호 (응답/결과) | Finish 진입 트리거 |

## 2. 상태 전이 기준 (In/Out 기준)

```
Out ON  →  Ready → Going
In  ON  →  Going → Finish
In  OFF →  Finish → Ready
```

## 3. Direction별 전이 조건

### 3.1 InOut (Out + In 모두 존재)

```
      Out ON            In ON            In OFF
Ready ──────► Going ──────────► Finish ─────────► Ready
```

| 전이 | 조건 |
|------|------|
| Ready → Going | Out 신호 활성 |
| Going → Finish | In 신호 수신 |
| Finish → Ready | In 신호 해제(OFF) |

**특징:**
- Out 신호가 작업 시작을 트리거
- In 신호가 작업 완료를 알림
- In 신호가 꺼지면 Ready로 복귀

---

### 3.2 InOnly (In만 존재, Out 없음)

In ON 시 Going을 순간 경유해 즉시 Finish로 안착. In OFF 시 Ready 복귀.

```
      In ON     (즉시)     In OFF
Ready ──────► Going ──► Finish ──────► Ready
```

| 전이 | 조건 |
|------|------|
| Ready → Going | In 신호 ON |
| Going → Finish | In ON 즉시 (Going은 순간 경유) |
| Finish → Ready | In 신호 OFF |

**특이사항:**
- Going은 상태 순서 보장을 위한 순간 경유
- Finish가 In ON 동안의 안정 상태
- In이 꺼질 때 Finish → Ready로 복귀

---

### 3.3 OutOnly (Out만 존재, In 없음)

Out 신호 ON/OFF가 Going/Finish를 결정한다.

```
      Out ON            Out OFF           자동
Ready ──────► Going ──────────► Finish ──────► Ready
```

| 전이 | 조건 |
|------|------|
| Ready → Going | Out 신호 ON |
| Going → Finish | Out 신호 OFF |
| Finish → Ready | 자동 즉시 복귀 |

**특이사항:**
- Going은 Out 신호가 살아 있는 동안만 유지
- 신호가 꺼지는 순간 Finish로 전이
- Finish에서 자동으로 즉시 Ready 복귀

---

## 4. 구현 참고사항

### Edge Detection
- **Rising Edge (0→1)**: 신호 활성화 감지
- **Falling Edge (1→0)**: 신호 비활성화 감지

### Direction 판단
```csharp
var direction = (hasInTag, hasOutTag) switch
{
    (true, true)   => "InOut",
    (true, false)  => "InOnly",
    (false, true)  => "OutOnly",
    (false, false) => "None"    // 에러: 태그 없음
};
```

### Cycle Count 증가 시점
- **InOut**: Finish 진입 시
- **InOnly**: Finish 진입 시
- **OutOnly**: Finish 진입 시

### Duration 측정
- **시작**: Ready → Going 진입 시 타임스탬프 기록
- **종료**: Going → Finish 진입 시 타임스탬프 기록
- **계산**: 종료 - 시작

---

## 5. 예시 시나리오

### InOut: 컨베이어 이송
1. Out 신호 ON (센서 감지) → Ready → Going
2. 이송 중...
3. In 신호 ON (완료 센서) → Going → Finish
4. In 신호 OFF (센서 해제) → Finish → Ready

### InOnly: 센서 감지
1. In 신호 ON (물체 감지) → Ready → Going → Finish
2. In 신호 ON 유지 중 (Finish 상태 유지)
3. In 신호 OFF (물체 벗어남) → Finish → Ready

### OutOnly: 단순 액추에이터
1. Out 신호 ON (작동 명령) → Ready → Going
2. Out 신호 OFF (명령 해제) → Going → Finish → Ready (자동)
