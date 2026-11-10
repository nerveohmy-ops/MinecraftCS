#include <bgfx/bgfx.h>
#include <cstdint>
#define EXPORT extern "C" __declspec(dllexport)

namespace {
    struct Vertex { float x, y, z, nx, ny, nz, u, v; uint32_t color; };
    bgfx::VertexLayout layout;
    bgfx::ProgramHandle program = BGFX_INVALID_HANDLE;
    bgfx::TextureHandle atlas = BGFX_INVALID_HANDLE;
    bgfx::UniformHandle sampler = BGFX_INVALID_HANDLE;
}

EXPORT void MinecraftBgfxShutdown() {
    if (bgfx::isValid(sampler)) bgfx::destroy(sampler);
    if (bgfx::isValid(atlas)) bgfx::destroy(atlas);
    if (bgfx::isValid(program)) bgfx::destroy(program);
    sampler = BGFX_INVALID_HANDLE;
    atlas = BGFX_INVALID_HANDLE;
    program = BGFX_INVALID_HANDLE;
    bgfx::shutdown();
}

EXPORT int MinecraftBgfxInit(void* hwnd, uint16_t width, uint16_t height,
    const void* vs, uint32_t vsSize, const void* fs, uint32_t fsSize,
    const void* pixels, uint16_t textureWidth, uint16_t textureHeight) {
    bgfx::renderFrame();
    bgfx::Init init;
    init.type = bgfx::RendererType::Direct3D11;
    init.swapChain.nwh = hwnd;
    init.swapChain.width = width;
    init.swapChain.height = height;
    if (!bgfx::init(init)) return 0;
    layout.begin()
        .add(bgfx::Attrib::Position, 3, bgfx::AttribType::Float)
        .add(bgfx::Attrib::Normal, 3, bgfx::AttribType::Float)
        .add(bgfx::Attrib::TexCoord0, 2, bgfx::AttribType::Float)
        .add(bgfx::Attrib::Color0, 4, bgfx::AttribType::Uint8, true)
        .end();

    auto vertex = bgfx::createShader(bgfx::copy(vs, vsSize));
    auto fragment = bgfx::createShader(bgfx::copy(fs, fsSize));
    if (bgfx::isValid(vertex) && bgfx::isValid(fragment))
        program = bgfx::createProgram(vertex, fragment, true);
    else {
        if (bgfx::isValid(vertex)) bgfx::destroy(vertex);
        if (bgfx::isValid(fragment)) bgfx::destroy(fragment);
    }
    sampler = bgfx::createUniform("s_atlas", bgfx::UniformType::Sampler);
    atlas = bgfx::createTexture2D(textureWidth, textureHeight, false, 1, bgfx::TextureFormat::RGBA8,
        BGFX_SAMPLER_POINT | BGFX_SAMPLER_U_CLAMP | BGFX_SAMPLER_V_CLAMP,
        bgfx::copy(pixels, uint32_t(textureWidth) * textureHeight * 4));
    if (!bgfx::isValid(program) || !bgfx::isValid(atlas) || !bgfx::isValid(sampler)) {
        MinecraftBgfxShutdown();
        return 0;
    }
    bgfx::setViewClear(0, BGFX_CLEAR_COLOR | BGFX_CLEAR_DEPTH, 0x83b8d2ff, 1.0f, 0);
    return 1;
}

EXPORT uint32_t MinecraftBgfxUpload(const Vertex* vertices, uint32_t vertexCount,
    const uint16_t* indices, uint32_t indexCount) {
    auto vb = bgfx::createVertexBuffer(bgfx::copy(vertices, vertexCount * sizeof(Vertex)), layout);
    auto ib = bgfx::createIndexBuffer(bgfx::copy(indices, indexCount * sizeof(uint16_t)));
    if (!bgfx::isValid(vb) || !bgfx::isValid(ib)) {
        if (bgfx::isValid(vb)) bgfx::destroy(vb);
        if (bgfx::isValid(ib)) bgfx::destroy(ib);
        return UINT32_MAX;
    }
    return uint32_t(vb.idx) | (uint32_t(ib.idx) << 16);
}

EXPORT void MinecraftBgfxRelease(uint32_t mesh) {
    bgfx::destroy(bgfx::VertexBufferHandle{uint16_t(mesh)});
    bgfx::destroy(bgfx::IndexBufferHandle{uint16_t(mesh >> 16)});
}

EXPORT void MinecraftBgfxBegin(const float* view, const float* projection, uint16_t width, uint16_t height) {
    bgfx::setViewRect(0, 0, 0, width, height);
    bgfx::setViewTransform(0, view, projection);
    bgfx::touch(0);
}

EXPORT void MinecraftBgfxDraw(uint32_t mesh) {
    bgfx::setVertexBuffer(0, bgfx::VertexBufferHandle{uint16_t(mesh)});
    bgfx::setIndexBuffer(bgfx::IndexBufferHandle{uint16_t(mesh >> 16)});
    bgfx::setTexture(0, sampler, atlas);
    bgfx::setState(BGFX_STATE_WRITE_RGB | BGFX_STATE_WRITE_A | BGFX_STATE_WRITE_Z | BGFX_STATE_DEPTH_TEST_LESS);
    bgfx::submit(0, program);
}

EXPORT void MinecraftBgfxResize(uint16_t width, uint16_t height) {
    bgfx::SwapChain swapChain;
    swapChain.width = width;
    swapChain.height = height;
    bgfx::reset(BGFX_RESET_NONE, &swapChain);
}
EXPORT uint32_t MinecraftBgfxFrame() { bgfx::frame(); return bgfx::getStats()->numDraw; }
EXPORT uint16_t MinecraftBgfxVendor() { return bgfx::getCaps()->vendorId; }
EXPORT void MinecraftBgfxScreenshot(const char* path) { bgfx::requestScreenShot(BGFX_INVALID_HANDLE, path); }
