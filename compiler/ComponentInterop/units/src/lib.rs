//! `acme:units/units`, as the Nanyx component in ../shapes declares it with `@import`.

wit_bindgen::generate!({ path: "wit", world: "provider" });

use exports::acme::units::units::{Guest, Length, Unit};

struct Units;

impl Guest for Units {
    fn to_metres(p0: Length) -> f64 {
        match p0.unit {
            Unit::Metres => p0.value,
            Unit::Feet => p0.value * 0.3048,
        }
    }

    fn label(p0: Unit) -> String {
        match p0 {
            Unit::Metres => "metres".to_string(),
            Unit::Feet => "feet".to_string(),
        }
    }
}

export!(Units);
