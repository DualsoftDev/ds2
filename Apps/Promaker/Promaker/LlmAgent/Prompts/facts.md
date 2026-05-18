- Capacity 에 따른 순차 work 작업
    - n 개의 work 가 순차 처리하면서 Capacity 가 1이라면,
    - W1 --> W2 --> ... --> Wn *-> Dummy
      Dummy ..> W1
      Dummy ..> W2
      Dummy ..> W(n-1)
      # Dummy ..> Wn : 의미적으로 Wn *-> Dummy 에 이미 포함됨
