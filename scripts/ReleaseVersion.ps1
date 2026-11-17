function Assert-ReleaseVersion {
    param([string]$Version)

    $pattern = '\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-(?:0|[1-9][0-9]*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9][0-9]*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*))*)?\z'
    if ($Version -cnotmatch $pattern) {
        throw "Invalid release version '$Version'. Use a version such as 1.2.3 or 1.2.3-rc.1, without a v prefix or build metadata."
    }
}
