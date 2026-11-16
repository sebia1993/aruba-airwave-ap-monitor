#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <shellapi.h>
#include <string>
#include <vector>

namespace {
std::wstring QuoteArgument(const std::wstring& value) {
    std::wstring result = L"\"";
    size_t slashes = 0;
    for (wchar_t character : value) {
        if (character == L'\\') { ++slashes; continue; }
        result.append(character == L'"' ? slashes * 2 + 1 : slashes, L'\\');
        result += character;
        slashes = 0;
    }
    result.append(slashes * 2, L'\\');
    return result + L'"';
}

void WriteError(const std::wstring& message) {
    const auto text = message + L"\r\n";
    OutputDebugStringW(text.c_str());
    HANDLE error = GetStdHandle(STD_ERROR_HANDLE);
    if (error == nullptr || error == INVALID_HANDLE_VALUE) return;
    DWORD written = 0;
    if (GetFileType(error) == FILE_TYPE_CHAR) {
        WriteConsoleW(error, text.c_str(), static_cast<DWORD>(text.size()), &written, nullptr);
        return;
    }
    const int length = WideCharToMultiByte(CP_UTF8, 0, text.c_str(), -1, nullptr, 0, nullptr, nullptr);
    if (length <= 1) return;
    std::vector<char> bytes(static_cast<size_t>(length));
    WideCharToMultiByte(CP_UTF8, 0, text.c_str(), -1, bytes.data(), length, nullptr, nullptr);
    WriteFile(error, bytes.data(), static_cast<DWORD>(length - 1), &written, nullptr);
}

int Fail(const std::wstring& message, bool noBrowser, DWORD code = 0) {
    std::wstring full = message;
    if (code != 0) full += L"\nWindows 오류 코드: " + std::to_wstring(code);
    WriteError(full);
    if (!noBrowser) MessageBoxW(nullptr, full.c_str(), L"AirWave AP Monitor", MB_OK | MB_ICONERROR);
    return 1;
}

std::wstring ModulePath() {
    for (DWORD size = 512; size <= 32768; size *= 2) {
        std::vector<wchar_t> buffer(size);
        const DWORD length = GetModuleFileNameW(nullptr, buffer.data(), size);
        if (length == 0) return {};
        if (length < size) return std::wstring(buffer.data(), length);
    }
    return {};
}
}

int WINAPI wWinMain(HINSTANCE, HINSTANCE, PWSTR, int) {
    int count = 0;
    LPWSTR* parsed = CommandLineToArgvW(GetCommandLineW(), &count);
    if (parsed == nullptr) return Fail(L"실행 인수를 읽지 못했습니다.", false, GetLastError());
    std::vector<std::wstring> arguments;
    for (int index = 1; index < count; ++index) arguments.emplace_back(parsed[index]);
    LocalFree(parsed);
    bool noBrowser = false;
    for (size_t index = 0; index + 1 < arguments.size(); ++index)
        if (arguments[index] == L"--no-browser" && arguments[index + 1] == L"true") noBrowser = true;

    const std::wstring module = ModulePath();
    const size_t separator = module.find_last_of(L"\\/");
    if (module.empty() || separator == std::wstring::npos)
        return Fail(L"실행 파일의 위치를 확인하지 못했습니다.", noBrowser, GetLastError());
    const std::wstring appDirectory = module.substr(0, separator) + L"\\app";
    const std::wstring executable = appDirectory + L"\\AirWaveApMonitor.App.exe";
    // These files are required before managed startup can display its own precise errors.
    for (const wchar_t* name : { L"AirWaveApMonitor.App.exe", L"AirWaveApMonitor.App.dll",
            L"AirWaveApMonitor.App.runtimeconfig.json", L"hostfxr.dll", L"hostpolicy.dll", L"coreclr.dll" }) {
        const DWORD attributes = GetFileAttributesW((appDirectory + L"\\" + name).c_str());
        if (attributes == INVALID_FILE_ATTRIBUTES || (attributes & FILE_ATTRIBUTE_DIRECTORY) != 0)
            return Fail(L"필수 앱 파일이 없습니다: " + std::wstring(name) + L"\nZIP 전체를 새 폴더에 압축 해제한 뒤 최상위 AirWaveAPMonitor.exe를 실행하세요.", noBrowser);
    }
    std::wstring command = QuoteArgument(executable) + L" --desktop true";
    for (const auto& argument : arguments) command += L" " + QuoteArgument(argument);
    if (command.size() >= 32767) return Fail(L"실행 인수가 너무 깁니다.", noBrowser);
    STARTUPINFOW startup{};
    startup.cb = static_cast<DWORD>(sizeof(startup));
    PROCESS_INFORMATION child{};
    // Explicit application path and non-inherited handles avoid PATH/quoting ambiguities.
    // The backend console owns the interactive collector lifetime; no service is installed.
    if (!CreateProcessW(executable.c_str(), command.data(), nullptr, nullptr, FALSE,
            CREATE_NEW_CONSOLE, nullptr, appDirectory.c_str(), &startup, &child))
        return Fail(L"수집기 프로그램을 시작하지 못했습니다. 압축 해제 위치와 실행 권한을 확인하세요.", noBrowser, GetLastError());
    CloseHandle(child.hThread);
    const DWORD waited = WaitForSingleObject(child.hProcess, INFINITE);
    DWORD exitCode = 1;
    const BOOL gotExitCode = waited == WAIT_OBJECT_0 && GetExitCodeProcess(child.hProcess, &exitCode);
    const DWORD waitError = gotExitCode ? 0 : GetLastError();
    CloseHandle(child.hProcess);
    if (!gotExitCode) return Fail(L"수집기 종료 상태를 확인하지 못했습니다.", noBrowser, waitError);
    // Managed startup already displays exit=1 errors. Console close/Ctrl+C is intentional.
    if (exitCode != 0 && exitCode != 1 && exitCode != 0xC000013A)
        (void)Fail(L"수집기가 비정상 종료되었습니다. 앱 파일과 콘솔의 오류를 확인하세요.", noBrowser, exitCode);
    return static_cast<int>(exitCode);
}
