namespace Ds2.Core


/// 위치 및 크기 정보 (UI용)
type Xywh(x: int, y: int, w: int, h: int) =
    member val X: int = x with get, set
    member val Y: int = y with get, set
    member val W: int = w with get, set
    member val H: int = h with get, set

/// I/O 태그 (HW 매핑용)
type IOTag() =
    member val Name: string = "" with get, set          // 논리 이름
    member val Address: string = "" with get, set       // PLC 물리 주소
    member val Description: string = "" with get, set   // 설명
    member val DataType: IOTagDataType = IOTagDataType.BOOL with get, set
    member val DefaultValue: obj option = None with get, set

    new(name: string, addr: string, desc: string) as this =
        IOTag() then
            this.Name <- name
            this.Address <- addr
            this.Description <- desc
