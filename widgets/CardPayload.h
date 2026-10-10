#pragma once
#include <stdexcept>
#include <winrt/Windows.Data.Json.h>
#include <utility>
#include <string>

inline std::pair<winrt::hstring,winrt::hstring> ParseCards(std::string const& utf8)
{
    if(utf8.empty()||utf8.size()>262144)throw std::runtime_error("Invalid widget payload");
    auto root=winrt::Windows::Data::Json::JsonObject::Parse(winrt::to_hstring(utf8));
    auto medium=root.GetNamedObject(L"medium"),large=root.GetNamedObject(L"large");
    for(auto const& card:{medium,large})
        if(card.GetNamedString(L"type")!=L"AdaptiveCard"||card.GetNamedArray(L"body").Size()!=3)
            throw std::runtime_error("Invalid widget payload");
    return {medium.Stringify(),large.Stringify()};
}
