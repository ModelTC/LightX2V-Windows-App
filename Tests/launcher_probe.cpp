#define UNICODE
#define _UNICODE
#include <windows.h>
#include <string>
#include <shellapi.h>
int WINAPI wWinMain(HINSTANCE, HINSTANCE, PWSTR, int) {
    int argc=0; LPWSTR* argv=CommandLineToArgvW(GetCommandLineW(), &argc);
    if (!argv) return 5;
    wchar_t destination[32768];
    if (!GetEnvironmentVariableW(L"LIGHTX2V_LAUNCHER_PROBE", destination, 32768)) return 2;
    std::wstring content;
    for (int i = 0; i < argc; ++i) { content += argv[i]; content += L'\0'; }
    LocalFree(argv);
    HANDLE output = CreateFileW(destination, GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (output == INVALID_HANDLE_VALUE) return 3;
    DWORD written = 0;
    BOOL okay = WriteFile(output, content.data(), static_cast<DWORD>(content.size() * sizeof(wchar_t)), &written, nullptr);
    CloseHandle(output);
    return okay ? 0 : 4;
}
