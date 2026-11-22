//! `acme:units/units`, as the Nanyx component in ../shapes declares it with `@import`.

wit_bindgen::generate!({ path: "wit", world: "provider" });

use exports::acme::units::units::{Guest, Length, Unit};

struct Units;

impl Guest for Units {
    fn to_metres(length: Length) -> f64 {
        match length.unit {
            Unit::Metres => length.value,
            Unit::Feet => length.value * 0.3048,
        }
    }

    fn label(unit: Unit) -> String {
        match unit {
            Unit::Metres => "metres".to_string(),
            Unit::Feet => "feet".to_string(),
        }
    }
}

export!(Units);
