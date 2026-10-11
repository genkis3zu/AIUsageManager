#pragma once
#include <cstdint>
#include <map>
#include <string>
#include <algorithm>

// Calls are serialized by the provider's mutex. No WinRT callback objects are stored.
struct WidgetSlot { bool large=false; bool active=false; };
class ProviderState
{
    std::map<std::wstring,WidgetSlot> slots_;
    bool refreshing_=false,requested_=false;
    uint64_t next_=0;
public:
    void restore(std::wstring const& id,bool large,bool active){slots_[id]={large,active};requested_|=active;}
    void activate(std::wstring const& id,bool large){restore(id,large,true);}
    void deactivate(std::wstring const& id){auto i=slots_.find(id);if(i!=slots_.end())i->second.active=false;}
    void remove(std::wstring const& id){slots_.erase(id);}
    bool contains(std::wstring const& id) const{return slots_.contains(id);}
    size_t active_count() const{return static_cast<size_t>(std::count_if(slots_.begin(),slots_.end(),[](auto const& pair){return pair.second.active;}));}
    bool due(uint64_t now) const{return !refreshing_&&active_count()>0&&(requested_||now>=next_);}
    bool begin_refresh(uint64_t now){if(!due(now))return false;refreshing_=true;requested_=false;return true;}
    void finish_refresh(uint64_t now){refreshing_=false;next_=now+300000;}
    auto snapshot() const{return slots_;}
};
