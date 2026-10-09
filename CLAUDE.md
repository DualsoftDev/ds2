# ds2 Repository Guidelines

## 솔루션 규칙

이 저장소의 솔루션은 [Solutions/Ds2.sln](Solutions/Ds2.sln) 하나다. 프로젝트(`.fsproj`/`.csproj`)를 새로 만들거나 지우면 반드시 이 솔루션에도 같이 반영한다 (`dotnet sln Solutions/Ds2.sln add|remove`).

## 소비 저장소와 경로 안정성

다른 저장소(ds2-Promaker, ds2-AasxEditor 등)가 이 저장소를 `external/ds2` 서브모듈로 두고, `external/ds2/Solutions/<경로>/<프로젝트>.fsproj` 를 ProjectReference 와 sln 항목으로 **직접** 참조한다.

- 프로젝트 디렉터리나 파일명을 옮기거나 바꾸면 소비 저장소의 참조가 깨진다. 바꿔야 하면 소비 저장소의 ProjectReference·sln 경로를 같이 고치고 서브모듈 커밋을 올린다.
- `Solutions/Directory.Build.props` · `Directory.Packages.props` 는 소비 저장소에 사본이 있다. 패키지 버전을 올리면 사본도 맞춘다.
- `Ds2.TestKit` 은 소비 저장소의 테스트도 가져다 쓴다. 공개 API 를 줄일 때 소비자 빌드를 확인한다.

**이유**: 서브모듈 소비자는 이 저장소의 커밋을 고정해 쓰므로, 경로가 바뀐 커밋으로 올리는 순간 소비자 빌드가 깨진다.
