namespace Ds2.SymbolImport.Matching

open System.Collections.Generic

module PrefixTrie =

    /// <summary>
    /// 접두사 검색을 위한 Trie 노드
    /// </summary>
    type TrieNode() = // Children, IsTerminal, Value
        let children = Dictionary<char, TrieNode>()
        let mutable isTerminal = false
        let mutable value: string option = None

        member _.Children = children
        member _.IsTerminal with get() = isTerminal and set v = isTerminal <- v
        member _.Value with get() = value and set v = value <- v

    /// <summary>
    /// 문자열 접두사 리스트로부터 Trie 구조 생성
    /// </summary>
    /// <param name="prefixes">Trie에 삽입할 접두사 문자열 시퀀스</param>
    /// <returns>생성된 Trie의 루트 TrieNode</returns>
    let buildPrefixTrie (prefixes: string seq) : TrieNode =
        let root = TrieNode()
        for prefix in prefixes do
            let mutable node = root
            for ch in prefix do
                if not (node.Children.ContainsKey ch) then
                    node.Children.[ch] <- TrieNode()
                node <- node.Children.[ch]
            node.IsTerminal <- true
            node.Value <- Some prefix
        root

    /// <summary>
    /// 주어진 입력 문자열에 대해 Trie에서 가장 긴 매칭 접두사 찾기
    /// </summary>
    /// <param name="root">Trie의 루트 TrieNode</param>
    /// <param name="input">매칭 접두사를 검색할 입력 문자열</param>
    /// <returns>Some(가장 긴 매칭 접두사) 또는 매칭 없으면 None</returns>
    let tryFindLongestPrefix (root: TrieNode) (input: string) : string option =
        let mutable node = root
        let mutable result = None

        input
        |> Seq.tryPick (fun ch ->
            match node.Children.TryGetValue ch with
            | true, next ->
                node <- next
                if node.IsTerminal then result <- node.Value
                None
            | _ -> Some() // 매칭 없으면 조기 종료
        )
        |> ignore

        result

    /// <summary>
    /// 주어진 접두사로 시작하는 Trie의 모든 문자열 찾기
    /// 빠름: 선형 검색을 피하고 Trie 구조 사용
    /// </summary>
    /// <param name="root">Trie의 루트 TrieNode</param>
    /// <param name="prefix">검색할 접두사 문자열</param>
    /// <returns>접두사로 시작하는 모든 문자열 리스트</returns>
    let findAllWithPrefix (root: TrieNode) (prefix: string) : string list =
        // 접두사 노드로 이동
        let mutable node = root
        let mutable found = true

        for ch in prefix do
            match node.Children.TryGetValue ch with
            | true, next -> node <- next
            | false, _ ->
                found <- false

        if not found then
            []
        else
            // 이 지점부터 모든 터미널 노드 수집
            let results = System.Collections.Generic.List<string>()

            let rec collectAll (currentNode: TrieNode) =
                if currentNode.IsTerminal then
                    match currentNode.Value with
                    | Some v -> results.Add(v)
                    | None -> ()

                for kvp in currentNode.Children do
                    collectAll kvp.Value

            collectAll node
            results |> List.ofSeq
