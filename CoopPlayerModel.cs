using System.Numerics;
using Raylib_cs;

namespace MinecraftCS;

internal static unsafe partial class Program
{
    // Геометрия в пропорциях классического блочного персонажа: 8x8 голова,
    // 8x12 корпус и 4x12 конечности. Масштаб сохраняет игровую высоту 1,8 блока.
    const float SkinPixel = 1.8f / 32;
    static Vector3 modelPosition;
    static float modelYaw, modelPitch, modelCrouch, modelStride, modelPhase, modelAir;
    static bool modelReady;

    static void ResetFriendModel()
    {
        modelReady = false;
        modelCrouch = modelStride = modelPhase = modelAir = 0;
        friendSwing = 0;
    }

    static float SmoothModelAngle(float current, float target, float blend) =>
        current + MathF.Atan2(MathF.Sin(target - current), MathF.Cos(target - current)) * blend;

    static void UpdateFriendModel(float dt)
    {
        if (!coopMatched || friend == null) { ResetFriendModel(); return; }
        if (!modelReady || Vector3.DistanceSquared(modelPosition, friend.Position) > 64)
        {
            // Телепорт и новая сессия не должны растягивать интерполяцию через весь мир.
            modelPosition = friend.Position;
            modelYaw = friend.Yaw;
            modelPitch = friend.Pitch;
            modelReady = true;
        }
        float blend = 1 - MathF.Exp(-dt * 14);
        Vector3 previous = modelPosition;
        modelPosition = Vector3.Lerp(modelPosition, friend.Position, blend);
        modelYaw = SmoothModelAngle(modelYaw, friend.Yaw, blend);
        modelPitch += (friend.Pitch - modelPitch) * blend;
        modelCrouch += ((friend.Crouch ? 1 : 0) - modelCrouch) * blend;
        float distance = Vector2.Distance(new(previous.X, previous.Z), new(modelPosition.X, modelPosition.Z));
        float speed = distance / MathF.Max(dt, 0.001f);
        // У клиента Ground не передаётся: опору проверяем по блокам под ногами.
        bool grounded = !friend.Fly && Get((int)MathF.Floor(friend.X),
            (int)MathF.Floor(friend.Y - 0.08f), (int)MathF.Floor(friend.Z)) != Air;
        float stride = grounded ? Math.Clamp(speed / WalkSpeed, 0, 1.5f) : 0;
        modelStride += (stride - modelStride) * blend;
        modelAir += ((!grounded ? 1 : 0) - modelAir) * blend;
        modelPhase = (modelPhase + distance * 2.8f) % MathF.Tau;
    }

    // Все детали рисуются в локальных координатах; матрицы вращают их вокруг суставов.
    static void ModelBox(float x, float y, float z, float width, float height, float depth, Color color) =>
        Raylib.DrawCube(new Vector3(x, y, z) * SkinPixel,
            width * SkinPixel, height * SkinPixel, depth * SkinPixel, color);

    static void DrawModelLimb(float x, float y, float angle, float roll, bool arm)
    {
        Rlgl.PushMatrix();
        Rlgl.Translatef(x * SkinPixel, y * SkinPixel, 0);
        Rlgl.Rotatef(angle, 1, 0, 0);
        Rlgl.Rotatef(roll, 0, 0, 1);
        if (arm)
        {
            ModelBox(0, -2, 0, 4, 4, 4, Col(35,151,164)); // Короткий рукав.
            ModelBox(0, -8, 0, 4, 8, 4, Col(181,133,100));
            ModelBox(0, -7, 2.02f, 2, 5, 0.06f, Col(193,144,108)); // Пиксельный блик кожи.
            ModelBox(0, -11, 0, 4.02f, 2, 4.02f, Col(169,119,88));
        }
        else
        {
            ModelBox(0, -5, 0, 4, 10, 4, Col(65,63,153));
            ModelBox(0, -5, 2.02f, 2, 6, 0.06f, Col(74,73,167));
            ModelBox(0, -11, 0, 4, 2, 4, Col(65,61,67)); // Отдельная полоса ботинка.
        }
        Rlgl.PopMatrix();
    }

    static void DrawAnimatedFriend()
    {
        if (!modelReady) return;
        float gait = MathF.Sin(modelPhase) * modelStride * 34;
        float idle = MathF.Sin(animationTime * 1.8f) * 2;
        float attack = friendSwing > 0 ? MathF.Sin(friendSwing / 0.26f * MathF.PI) : 0;
        Rlgl.PushMatrix();
        Rlgl.Translatef(modelPosition.X, modelPosition.Y, modelPosition.Z);
        // Локальная передняя сторона +Z совмещается с направлением взгляда в игровом мире.
        Rlgl.Rotatef(90 - modelYaw * 180 / MathF.PI, 0, 1, 0);
        float hip = 12 - modelCrouch * 1.5f;
        DrawModelLimb(-2, hip, gait - modelAir * 15 - modelCrouch * 8, 0, false);
        DrawModelLimb(2, hip, -gait + modelAir * 15 - modelCrouch * 8, 0, false);

        Rlgl.PushMatrix();
        Rlgl.Translatef(0, hip * SkinPixel, 0);
        Rlgl.Rotatef(modelCrouch * 25, 1, 0, 0);
        ModelBox(0, 6, 0, 8, 12, 4, Col(31,153,166));
        ModelBox(0, 5.5f, 2.03f, 4, 7, 0.06f, Col(28,139,153));
        ModelBox(0, 10.5f, 2.05f, 2, 3, 0.08f, Col(181,133,100)); // Вырез воротника.
        ModelBox(0, 1, 0, 8.04f, 2, 4.04f, Col(30,130,146));
        DrawModelLimb(-6, 12, -gait + idle - attack * 95, -3 - attack * 12, true);
        DrawModelLimb(6, 12, gait - idle, 3, true);

        Rlgl.PushMatrix();
        Rlgl.Translatef(0, 12 * SkinPixel, 0);
        // Компенсируем наклон корпуса: при приседании голова продолжает смотреть на цель.
        Rlgl.Rotatef(-modelPitch * 180 / MathF.PI - modelCrouch * 25, 1, 0, 0);
        ModelBox(0, 4, 0, 8, 8, 8, Col(181,133,100));
        ModelBox(0, 7, 0, 8.12f, 2, 8.12f, Col(57,39,28)); // Волосы сверху и на затылке.
        ModelBox(0, 4.5f, -3.8f, 8.12f, 5, 0.6f, Col(57,39,28));
        ModelBox(-3.7f, 5.5f, 0, 0.7f, 3, 8.05f, Col(65,44,30));
        ModelBox(3.7f, 5.5f, 0, 0.7f, 3, 8.05f, Col(65,44,30));
        ModelBox(-2, 4.5f, 4.04f, 2, 1, 0.1f, Col(230,229,222));
        ModelBox(2, 4.5f, 4.04f, 2, 1, 0.1f, Col(230,229,222));
        ModelBox(-1.5f, 4.5f, 4.11f, 1, 1, 0.08f, Col(75,65,142));
        ModelBox(1.5f, 4.5f, 4.11f, 1, 1, 0.08f, Col(75,65,142));
        ModelBox(0, 3, 4.08f, 2, 1, 0.25f, Col(151,102,76)); // Нос и борода крупными пикселями.
        ModelBox(0, 1.5f, 4.05f, 4, 2, 0.12f, Col(82,52,38));
        ModelBox(0, 2, 4.13f, 2, 1, 0.08f, Col(126,78,58));
        Rlgl.PopMatrix();
        Rlgl.PopMatrix();
        Rlgl.PopMatrix();
    }
}
