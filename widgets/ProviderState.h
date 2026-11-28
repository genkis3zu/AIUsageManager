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
    bool refreshing_=false,requested_=false,byHandRequested_=false,byHand_=false;
    uint64_t next_=0;
    // A 更新 press waiting for the next fetch is dropped once no widget is active, so a later activation is not one by hand.
    void drop_by_hand_when_idle(){if(active_count()==0)byHandRequested_=false;}
public:
    void restore(std::wstring const& id,bool large,bool active){slots_[id]={large,active};requested_|=active;drop_by_hand_when_idle();}
    void activate(std::wstring const& id,bool large){restore(id,large,true);}
    void deactivate(std::wstring const& id){auto i=slots_.find(id);if(i!=slots_.end())i->second.active=false;drop_by_hand_when_idle();}
    void remove(std::wstring const& id){slots_.erase(id);drop_by_hand_when_idle();}
    bool contains(std::wstring const& id) const{return slots_.contains(id);}
    size_t active_count() const{return static_cast<size_t>(std::count_if(slots_.begin(),slots_.end(),[](auto const& pair){return pair.second.active;}));}
    bool due(uint64_t now) const{return !refreshing_&&active_count()>0&&(requested_||now>=next_);}
    // 更新 pressed on a widget: the next refresh is one by hand (the app then reuses another app's result for 180 seconds, not 270).
    void request_by_hand(){if(active_count()==0)return;requested_=true;byHandRequested_=true;}
    bool begin_refresh(uint64_t now){if(!due(now))return false;refreshing_=true;requested_=false;byHand_=byHandRequested_;byHandRequested_=false;return true;}
    bool by_hand() const{return byHand_;}
    void finish_refresh(uint64_t now){refreshing_=false;next_=now+300000;}
    auto snapshot() const{return slots_;}
};
