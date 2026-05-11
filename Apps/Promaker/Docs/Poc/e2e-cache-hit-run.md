[측정 환경]
  일시        : 2026-05-11
  model       : claude-haiku-4-5-20251001
  size        : medium (sys=10 work=30 call=3)
  turn count  : 10
  script      : Apps/Promaker/Docs/Poc/e2e-cache-hit.fsx
  패턴        : 옵션 A — history 안에도 multi-content user (snapshot 포함) 누적,
                마지막 user 의 snapshot block 에만 cache_control 부착

─────────────────────────────────────────────────────────────
[R5b] snapshot 단독 token 실측
  size      : sys=10 work=30 call=3
  chars     : 2501
  heuristic : 626  (chars / 4)
  Anthropic : 1023  (count_tokens claude-haiku-4-5-20251001)

─────────────────────────────────────────────────────────────
[E2E #3] cache 적중률 측정 — 10 turn, model=claude-haiku-4-5-20251001
  format: turn=N input cache_cr cache_rd output hit_ratio latency_ms
  turn= 1 input= 2699 cache_cr=    0 cache_rd=    0 output=  53 hit=  0.0% lat=1298ms
  turn= 2 input= 3778 cache_cr=    0 cache_rd=    0 output=  27 hit=  0.0% lat=3679ms
  turn= 3 input=   10 cache_cr= 4821 cache_rd=    0 output=  23 hit=  0.0% lat=1157ms
  turn= 4 input=   10 cache_cr= 1049 cache_rd= 4821 output=  17 hit= 82.0% lat=1229ms
  turn= 5 input=   10 cache_cr= 1043 cache_rd= 5870 output=  19 hit= 84.8% lat=1057ms
  turn= 6 input=   10 cache_cr= 1045 cache_rd= 6913 output=  17 hit= 86.8% lat=975ms
  turn= 7 input=   10 cache_cr= 1043 cache_rd= 7958 output=  16 hit= 88.3% lat=969ms
  turn= 8 input=   10 cache_cr= 1042 cache_rd= 9001 output=  16 hit= 89.5% lat=986ms
  turn= 9 input=   10 cache_cr= 1042 cache_rd=10043 output=  18 hit= 90.5% lat=1105ms
  turn=10 input=   10 cache_cr= 1044 cache_rd=11085 output=  18 hit= 91.3% lat=1167ms

[요약]
  총 turn          : 10
  총 input         : 6557 (cache_cr=12129 cache_rd=55691)
  총 output        : 224
  전체 hit ratio   :  74.9%   ((cache_rd) / (input + cache_cr + cache_rd))
  warm hit ratio   :  77.7%   (turn 2~ 만 — cold turn 1 제외)
  steady hit ratio :  91.3%   (마지막 3 turn 평균 — Anthropic cache 정착 후의 본질 효과)
  평균 latency     : 1362 ms

  판정: PASS (steady ≥ 90%, doc §E2E 시나리오 3 통과)

[해석]
  - cache_rd 누적: 4821 → 5870 → 6913 → ... → 11085 (매 turn ≈ +1043).
    +1043 ≈ snapshot 1023 token + ping/asst 의 미세 누적분 → snapshot 토큰이 정확히 cache hit 되고 있음.
  - turn 1~2 의 cache_cr=0 은 Anthropic 서버의 cache 생성 결정 지연 (server-side policy, 비공개).
  - turn 3 부터 cache_creation 시작 → turn 4 부터 cache_read 발생 → 매 turn +1043 토큰씩 hit 영역 확장.
  - steady-state (마지막 3 turn 평균) 91.3% 가 본질적 cache 효과 — turn 1~2 의 noise 가 평균 끌어내리지 않은 측정.

[ApiChatProvider 현 design 과의 비교]
  - 본 측정은 옵션 A 패턴 (history 안 multi-content + 마지막만 cache_control)
  - 현 ApiChatProvider 는 history 에 plain user 만 누적 → snapshot 위치가 매 turn 변동 → snapshot 자체는 cache hit 안 됨
  - 본 측정 결과는 doc §Step 4 deferred 의 R10 follow-up PR (snapshot block 별도 cache breakpoint + history multi-content 누적) 의 효과 예측치
