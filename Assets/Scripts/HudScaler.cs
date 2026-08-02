using UnityEngine;

// OnGUI 화면 표시를 **기준 해상도에 맞춰 일정 비율로** 그리기 위한 도우미.
//
// 문제: OnGUI는 좌표·폰트가 전부 '실제 픽셀'이다. 그래서 게임 창을 키우면 글자가
//       화면 대비 작아지고, 줄이면 커져서 레이아웃이 해상도마다 달라진다.
// 해결: GUI.matrix에 배율을 걸어 모든 좌표를 '기준 해상도 좌표계'로 다루면,
//       어떤 해상도에서도 화면에서 차지하는 비율이 같아진다.
//
// 사용법: 각 OnGUI() 맨 앞에서 HudScaler.Begin() 을 한 번 호출하고,
//         이후 좌표는 전부 기준 해상도(기본 1280×720) 기준으로 적는다.
public static class HudScaler
{
    // 이 해상도에서 '1배'로 보이도록 설계한다. BuildManager 인스펙터에서 바꿀 수 있다.
    public static Vector2 reference = new Vector2(1280f, 720f);

    // 가로/세로 중 작은 배율을 쓴다(레터박스 방식) — 어느 비율에서도 UI가 화면 밖으로 안 나간다.
    public static float Scale
    {
        get
        {
            float rw = Mathf.Max(1f, reference.x), rh = Mathf.Max(1f, reference.y);
            return Mathf.Max(0.01f, Mathf.Min(Screen.width / rw, Screen.height / rh));
        }
    }

    public static void Begin()
    {
        float s = Scale;
        GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(s, s, 1f));
    }

    // 실제 마우스 좌표(좌하단 원점) → 기준 해상도 GUI 좌표(좌상단 원점).
    // GUI.matrix를 걸면 히트 판정도 같이 변환해야 버튼 영역이 어긋나지 않는다.
    public static Vector2 MouseToGui(Vector2 screenMouse)
    {
        float s = Scale;
        return new Vector2(screenMouse.x / s, (Screen.height - screenMouse.y) / s);
    }
}
