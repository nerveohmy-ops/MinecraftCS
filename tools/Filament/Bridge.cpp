#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <filament/Camera.h>
#include <filament/Engine.h>
#include <filament/IndexBuffer.h>
#include <filament/Material.h>
#include <filament/MaterialInstance.h>
#include <filament/RenderableManager.h>
#include <filament/Renderer.h>
#include <filament/Scene.h>
#include <filament/SwapChain.h>
#include <filament/Texture.h>
#include <filament/TextureSampler.h>
#include <filament/VertexBuffer.h>
#include <filament/View.h>
#include <filament/Viewport.h>
#include <utils/EntityManager.h>
#include <algorithm>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <memory>
#include <string>
#include <vector>

#define EXPORT extern "C" __declspec(dllexport)
using namespace filament;
using namespace filament::math;

namespace {
    struct Vertex { float x, y, z, u, v; uint32_t color; };
    struct Mesh {
        VertexBuffer* vertices = nullptr;
        IndexBuffer* indices = nullptr;
        utils::Entity entity;
    };
    struct Readback { std::string path; uint32_t width, height; };

    Engine* engine = nullptr;
    HWND windowHandle = nullptr;
    bool closeRequested = false, captureMouse = false;

    LRESULT CALLBACK PreviewWindowProc(HWND hwnd, UINT message, WPARAM wparam, LPARAM lparam) {
        if (message == WM_CLOSE) { closeRequested = true; return 0; }
        if (message == WM_SETCURSOR && captureMouse && GetForegroundWindow() == hwnd) {
            SetCursor(nullptr);
            return TRUE;
        }
        return DefWindowProcW(hwnd, message, wparam, lparam);
    }
    SwapChain* swapChain = nullptr;
    Renderer* renderer = nullptr;
    View* view = nullptr;
    Scene* scene = nullptr;
    Camera* camera = nullptr;
    Material* material = nullptr;
    Texture* atlas = nullptr;
    utils::Entity cameraEntity;
    std::vector<utils::Entity> visible;
    uint32_t width = 0, height = 0;
    std::string screenshot;

    void FreeUpload(void* buffer, size_t, void*) { std::free(buffer); }
    void* CopyUpload(const void* data, size_t size) {
        void* copy = std::malloc(size);
        if (copy) std::memcpy(copy, data, size);
        return copy;
    }

    void WriteScreenshot(void* data, size_t, void* user) {
        std::unique_ptr<Readback> request(static_cast<Readback*>(user));
        FILE* file = nullptr;
        if (fopen_s(&file, request->path.c_str(), "wb") == 0 && file) {
            std::fprintf(file, "P6\n%u %u\n255\n", request->width, request->height);
            auto* rgba = static_cast<uint8_t*>(data);
            for (uint32_t y = 0; y < request->height; y++)
                for (uint32_t x = 0; x < request->width; x++)
                    std::fwrite(rgba + (size_t(y) * request->width + x) * 4, 1, 3, file);
            std::fclose(file);
        }
        std::free(data);
    }
}

EXPORT int MinecraftFilamentOpenWindow(int width, int height, int fullscreen, int interactive) {
    HINSTANCE app = GetModuleHandleW(nullptr);
    WNDCLASSW windowClass{};
    windowClass.lpfnWndProc = PreviewWindowProc;
    windowClass.hInstance = app;
    windowClass.hCursor = LoadCursor(nullptr, IDC_ARROW);
    windowClass.lpszClassName = L"MinecraftCSFilamentPreview";
    if (!RegisterClassW(&windowClass) && GetLastError() != ERROR_CLASS_ALREADY_EXISTS) return 0;
    DWORD style = fullscreen ? WS_POPUP : WS_OVERLAPPEDWINDOW;
    RECT bounds{0, 0, width, height};
    if (fullscreen) {
        MONITORINFO monitor{sizeof(MONITORINFO)};
        if (!GetMonitorInfoW(MonitorFromPoint({0, 0}, MONITOR_DEFAULTTOPRIMARY), &monitor)) return 0;
        bounds = monitor.rcMonitor;
    } else {
        AdjustWindowRect(&bounds, style, FALSE);
    }
    windowHandle = CreateWindowExW(0, windowClass.lpszClassName, L"MinecraftCS - Filament",
        style, fullscreen ? bounds.left : CW_USEDEFAULT, fullscreen ? bounds.top : CW_USEDEFAULT,
        bounds.right - bounds.left, bounds.bottom - bounds.top, nullptr, nullptr, app, nullptr);
    if (!windowHandle) return 0;
    closeRequested = false;
    captureMouse = interactive != 0;
    ShowWindow(windowHandle, SW_SHOW);
    return 1;
}

EXPORT void MinecraftFilamentCloseWindow() {
    if (!windowHandle) return;
    DestroyWindow(windowHandle);
    windowHandle = nullptr;
}

EXPORT int MinecraftFilamentPoll(uint16_t* clientWidth, uint16_t* clientHeight, int* keys, float* dx, float* dy) {
    MSG message{};
    while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE)) {
        TranslateMessage(&message);
        DispatchMessageW(&message);
    }
    if (closeRequested) return 0;
    RECT client{};
    GetClientRect(windowHandle, &client);
    *clientWidth = static_cast<uint16_t>(client.right);
    *clientHeight = static_cast<uint16_t>(client.bottom);
    *keys = 0;
    *dx = *dy = 0;
    if (!captureMouse || GetForegroundWindow() != windowHandle || IsIconic(windowHandle)) return 1;

    auto down = [](int key) { return (GetAsyncKeyState(key) & 0x8000) != 0; };
    if (down(VK_ESCAPE)) return 0;
    const int controls[] = {'W', 'S', 'D', 'A', VK_SPACE, VK_LCONTROL, VK_LSHIFT};
    for (int i = 0; i < 7; i++) if (down(controls[i])) *keys |= 1 << i;
    POINT center{client.right / 2, client.bottom / 2}, cursor{};
    if (ClientToScreen(windowHandle, &center) && GetCursorPos(&cursor)) {
        *dx = float(cursor.x - center.x);
        *dy = float(cursor.y - center.y);
        SetCursorPos(center.x, center.y);
    }
    return 1;
}

EXPORT void MinecraftFilamentShutdown() {
    if (!engine) return;
    engine->flushAndWait();
    if (view) engine->destroy(view);
    if (scene) engine->destroy(scene);
    if (camera) engine->destroyCameraComponent(cameraEntity);
    if (cameraEntity) utils::EntityManager::get().destroy(cameraEntity);
    if (material) engine->destroy(material);
    if (atlas) engine->destroy(atlas);
    if (renderer) engine->destroy(renderer);
    if (swapChain) engine->destroy(swapChain);
    Engine::destroy(&engine);
}

EXPORT int MinecraftFilamentInit(const void* package, uint32_t packageSize,
    const void* pixels, uint16_t atlasWidth, uint16_t atlasHeight) {
    engine = Engine::create(Engine::Backend::VULKAN);
    if (!engine) return 0;
    swapChain = engine->createSwapChain(windowHandle, SwapChain::CONFIG_READABLE);
    renderer = engine->createRenderer();
    scene = engine->createScene();
    view = engine->createView();
    cameraEntity = utils::EntityManager::get().create();
    camera = engine->createCamera(cameraEntity);
    material = Material::Builder().package(package, packageSize).build(*engine);
    atlas = Texture::Builder().width(atlasWidth).height(atlasHeight).levels(1)
        .sampler(Texture::Sampler::SAMPLER_2D).format(Texture::InternalFormat::RGBA8).build(*engine);
    size_t size = size_t(atlasWidth) * atlasHeight * 4;
    void* upload = CopyUpload(pixels, size);
    if (!material || !atlas || !upload) {
        std::free(upload);
        MinecraftFilamentShutdown();
        return 0;
    }
    atlas->setImage(*engine, 0, Texture::PixelBufferDescriptor(upload, size,
        Texture::Format::RGBA, Texture::Type::UBYTE, FreeUpload));
    material->getDefaultInstance()->setParameter("atlas", atlas,
        TextureSampler(TextureSampler::MinFilter::NEAREST, TextureSampler::MagFilter::NEAREST));
    view->setScene(scene);
    view->setCamera(camera);
    view->setPostProcessingEnabled(false);
    Renderer::ClearOptions clear;
    clear.clear = true;
    clear.clearColor = {0.25f, 0.48f, 0.64f, 1.0f};
    renderer->setClearOptions(clear);
    return 1;
}

EXPORT void* MinecraftFilamentUpload(const Vertex* data, uint32_t count, const uint16_t* indices, uint32_t indexCount) {
    size_t vertexSize = size_t(count) * sizeof(Vertex), indexSize = size_t(indexCount) * sizeof(uint16_t);
    void* vb = CopyUpload(data, vertexSize);
    void* ib = CopyUpload(indices, indexSize);
    if (!vb || !ib) { std::free(vb); std::free(ib); return nullptr; }
    auto mesh = std::make_unique<Mesh>();
    mesh->vertices = VertexBuffer::Builder().vertexCount(count).bufferCount(1)
        .attribute(VertexAttribute::POSITION, 0, VertexBuffer::AttributeType::FLOAT3, 0, sizeof(Vertex))
        .attribute(VertexAttribute::UV0, 0, VertexBuffer::AttributeType::FLOAT2, 12, sizeof(Vertex))
        .attribute(VertexAttribute::COLOR, 0, VertexBuffer::AttributeType::UBYTE4, 20, sizeof(Vertex))
        .normalized(VertexAttribute::COLOR).build(*engine);
    mesh->indices = IndexBuffer::Builder().indexCount(indexCount)
        .bufferType(IndexBuffer::IndexType::USHORT).build(*engine);
    mesh->vertices->setBufferAt(*engine, 0, VertexBuffer::BufferDescriptor(vb, vertexSize, FreeUpload));
    mesh->indices->setBuffer(*engine, IndexBuffer::BufferDescriptor(ib, indexSize, FreeUpload));
    mesh->entity = utils::EntityManager::get().create();
    float3 lower(data[0].x, data[0].y, data[0].z), upper = lower;
    for (uint32_t i = 1; i < count; i++) {
        float3 p(data[i].x, data[i].y, data[i].z);
        lower = min(lower, p);
        upper = max(upper, p);
    }
    if (RenderableManager::Builder(1)
        .boundingBox({(lower + upper) * 0.5f, (upper - lower) * 0.5f})
        .geometry(0, RenderableManager::PrimitiveType::TRIANGLES, mesh->vertices, mesh->indices)
        .material(0, material->getDefaultInstance()).castShadows(false).receiveShadows(false)
        .build(*engine, mesh->entity) != RenderableManager::Builder::Success) {
        engine->destroy(mesh->vertices);
        engine->destroy(mesh->indices);
        utils::EntityManager::get().destroy(mesh->entity);
        return nullptr;
    }
    return mesh.release();
}

EXPORT void MinecraftFilamentRelease(void* handle) {
    std::unique_ptr<Mesh> mesh(static_cast<Mesh*>(handle));
    scene->remove(mesh->entity);
    engine->destroy(mesh->entity);
    engine->destroy(mesh->vertices);
    engine->destroy(mesh->indices);
    utils::EntityManager::get().destroy(mesh->entity);
}

EXPORT void MinecraftFilamentBegin(float x, float y, float z, float dx, float dy, float dz,
    uint16_t viewportWidth, uint16_t viewportHeight) {
    width = viewportWidth;
    height = viewportHeight;
    view->setViewport({0, 0, width, height});
    camera->setProjection(70.0, double(width) / height, 0.05, 512.0, Camera::Fov::VERTICAL);
    camera->lookAt({x, y, z}, {x + dx, y + dy, z + dz}, {0, 1, 0});
}

EXPORT void MinecraftFilamentDraw(void* handle) {
    auto entity = static_cast<Mesh*>(handle)->entity;
    scene->addEntity(entity);
    visible.push_back(entity);
}

EXPORT void MinecraftFilamentResize() {
    engine->flushAndWait();
    engine->destroy(swapChain);
    swapChain = engine->createSwapChain(windowHandle, SwapChain::CONFIG_READABLE);
}

EXPORT void MinecraftFilamentScreenshot(const char* path) { screenshot = path; }

EXPORT uint32_t MinecraftFilamentFrame() {
    uint32_t draws = 0;
    if (renderer->beginFrame(swapChain)) {
        renderer->render(view);
        draws = static_cast<uint32_t>(visible.size());
        if (!screenshot.empty()) {
            size_t size = size_t(width) * height * 4;
            if (void* pixels = std::malloc(size)) {
                auto* request = new Readback{std::move(screenshot), width, height};
                renderer->readPixels(0, 0, width, height, Texture::PixelBufferDescriptor(pixels, size,
                    Texture::Format::RGBA, Texture::Type::UBYTE, WriteScreenshot, request));
            }
            screenshot.clear();
        }
        renderer->endFrame();
    }
    for (auto entity : visible) scene->remove(entity);
    visible.clear();
    return draws;
}
