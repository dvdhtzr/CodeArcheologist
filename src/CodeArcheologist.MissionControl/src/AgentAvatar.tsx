import { useId } from 'react';
import type { RoleId } from './demo';

/**
 * Original, inline SVG character artwork. MC1 does not download avatars or
 * require a remote image service. The component can be replaced by art assets
 * later without changing API contracts or the role state model.
 */
export function AgentAvatar({ role }: { role: RoleId }) {
  const key = useId().replace(/:/g, '');
  const ambient = key + '-ambient';
  const face = key + '-face';
  const jacket = key + '-jacket';
  const clip = key + '-clip';
  const accent = role === 'investigator' ? '#63d8ff' : role === 'skeptic' ? '#ffc17e' : '#ccb0ff';

  return (
    <svg className={'avatar-portrait avatar-portrait--' + role} viewBox="0 0 168 168"
      aria-hidden="true" focusable="false">
      <defs>
        <radialGradient id={ambient} cx="50%" cy="32%" r="80%">
          <stop offset="0" stopColor={accent} stopOpacity=".27" />
          <stop offset=".6" stopColor="#102039" stopOpacity=".96" />
          <stop offset="1" stopColor="#080e19" />
        </radialGradient>
        <linearGradient id={face} x1="0" y1="0" x2="1" y2="1">
          <stop offset="0" stopColor={role === 'skeptic' ? '#f2c299' : role === 'reviewer' ? '#d9cbcd' : '#c5cbd3'} />
          <stop offset="1" stopColor={role === 'skeptic' ? '#b8785c' : role === 'reviewer' ? '#907e9f' : '#879cad'} />
        </linearGradient>
        <linearGradient id={jacket} x1="0" y1="0" x2="1" y2="1">
          <stop offset="0" stopColor={role === 'skeptic' ? '#6e413d' : role === 'reviewer' ? '#493e72' : '#285979'} />
          <stop offset="1" stopColor="#101828" />
        </linearGradient>
        <clipPath id={clip}><circle cx="84" cy="84" r="77" /></clipPath>
      </defs>
      <circle cx="84" cy="84" r="80" fill="#060c17" stroke={accent} strokeOpacity=".31" />
      <circle cx="84" cy="84" r="77" fill={`url(#${ambient})`} />
      <g clipPath={`url(#${clip})`}>
        <g stroke={accent} strokeOpacity=".15" strokeWidth=".7">
          <path d="M5 48 H162 M5 123 H162 M24 3 V165 M145 3 V165" />
          <circle cx="84" cy="84" r="65" fill="none" />
          <circle cx="84" cy="84" r="53" fill="none" />
        </g>
        {role === 'investigator' && (
          <>
            <path d="M26 181 Q30 127 62 125 L105 125 Q143 130 150 181Z" fill={`url(#${jacket})`} />
            <path d="M62 128 L84 151 L107 127 L119 170 L50 170Z" fill="#172f46" />
            <path d="M76 110 L76 134 L84 142 L95 133 L95 109" fill={`url(#${face})`} />
            <ellipse cx="54" cy="91" rx="6" ry="11" fill="#a2aebb" />
            <ellipse cx="116" cy="91" rx="6" ry="11" fill="#a2aebb" />
            <path d="M56 69 Q57 37 84 36 Q115 35 116 70 L113 101 Q107 120 84 123 Q62 118 57 101Z" fill={`url(#${face})`} />
            <path d="M54 85 Q46 52 60 40 Q73 25 96 35 Q117 34 124 59 L117 82 L111 60 Q95 62 83 55 Q72 72 55 69Z" fill="#172f45" />
            <path d="M53 66 Q61 44 84 47 Q102 47 117 59" fill="none" stroke="#77dbf7" strokeOpacity=".45" strokeWidth="2" />
            <path d="M66 83 Q72 79 79 83 M92 83 Q99 79 105 83" fill="none" stroke="#263b4b" strokeWidth="3" strokeLinecap="round" />
            <circle cx="73" cy="85" r="2" fill="#183b50" /><circle cx="99" cy="85" r="2" fill="#183b50" />
            <path d="M86 88 L83 101 L89 103" fill="none" stroke="#657d8f" strokeWidth="1.6" strokeLinecap="round" />
            <path d="M76 110 Q85 114 94 109" fill="none" stroke="#667484" strokeWidth="1.7" strokeLinecap="round" />
            <path d="M46 76 Q83 68 122 77 L118 95 Q84 100 49 91Z" fill="#4ac9ff" fillOpacity=".17" stroke="#7ce2ff" strokeWidth="1.5" />
            <path d="M57 83 H75 M94 83 H111 M80 83 H90" stroke="#90edff" strokeOpacity=".8" strokeWidth="1.1" />
            <path d="M63 130 L82 152 L72 173 M106 130 L86 152 L99 173" fill="none" stroke="#70c8f2" strokeOpacity=".65" strokeWidth="2" />
            <circle cx="121" cy="91" r="4" fill="#69dcff" />
            <path d="M120 90 L139 81" stroke="#69dcff" strokeWidth="1.6" />
            <circle cx="140" cy="80" r="3" fill="#69dcff" fillOpacity=".5" />
          </>
        )}
        {role === 'skeptic' && (
          <>
            <path d="M47 68 Q39 25 83 25 Q134 25 122 91 L133 145 L41 149 L49 99Z" fill="#382932" />
            <path d="M25 180 Q30 133 62 124 L107 124 Q145 134 151 180Z" fill={`url(#${jacket})`} />
            <path d="M58 127 L83 148 L112 125 L105 177 L64 177Z" fill="#2c2732" />
            <path d="M75 109 V131 L84 140 L96 131 V110" fill={`url(#${face})`} />
            <ellipse cx="56" cy="88" rx="6" ry="11" fill="#be8264"/>
            <ellipse cx="113" cy="88" rx="6" ry="11" fill="#be8264"/>
            <path d="M57 65 Q62 42 84 41 Q110 41 115 66 L113 103 Q104 119 85 121 Q65 120 58 103Z" fill={`url(#${face})`} />
            <path d="M54 78 Q48 49 68 36 Q86 27 103 39 Q119 43 120 65 Q110 66 99 50 Q79 66 57 67Z" fill="#38282d" />
            <path d="M53 68 Q43 105 47 130 L58 119 L62 67" fill="#38282d" />
            <path d="M114 66 Q128 95 123 131 L111 118 L110 71" fill="#38282d" />
            <path d="M65 78 L81 76 M91 76 L107 78" stroke="#4d302d" strokeWidth="2.6" strokeLinecap="round" />
            <path d="M65 86 Q73 82 80 86 M92 86 Q99 82 106 86" fill="none" stroke="#3d2e30" strokeWidth="2.5" strokeLinecap="round" />
            <circle cx="74" cy="87" r="2" fill="#322834"/><circle cx="99" cy="87" r="2" fill="#322834"/>
            <path d="M85 89 L82 100 L88 102" fill="none" stroke="#95694f" strokeWidth="1.6"/>
            <path d="M77 111 Q85 108 94 110" fill="none" stroke="#77514b" strokeWidth="1.7" strokeLinecap="round" />
            <path d="M61 77 L82 75 L82 94 L64 95Z M88 76 L110 77 L108 95 L88 94Z M82 82 H89" fill="#ffcb73" fillOpacity=".09" stroke="#ffc17e" strokeWidth="1.8" />
            <path d="M60 85 L52 82 M110 85 L119 82" stroke="#ffc17e" strokeWidth="1.5"/>
            <path d="M62 129 L84 151 L110 129" fill="none" stroke="#f4b57b" strokeOpacity=".7" strokeWidth="2"/>
            <path d="M40 154 L64 141 M127 154 L103 141" stroke="#a46c58" strokeWidth="2" />
          </>
        )}
        {role === 'reviewer' && (
          <>
            <path d="M32 179 Q38 132 63 124 L107 124 Q139 134 146 179Z" fill={`url(#${jacket})`} />
            <path d="M64 124 L85 145 L107 124 L117 176 L53 176Z" fill="#292c48" />
            <path d="M75 109 V132 L85 140 L96 132 V110" fill={`url(#${face})`} />
            <ellipse cx="56" cy="89" rx="6" ry="10" fill="#a79aa8"/>
            <ellipse cx="113" cy="89" rx="6" ry="10" fill="#a79aa8"/>
            <path d="M58 65 Q61 42 85 39 Q112 43 115 66 L112 102 Q103 121 85 121 Q63 118 58 102Z" fill={`url(#${face})`} />
            <path d="M53 73 Q47 44 66 37 Q75 20 95 33 Q119 30 124 56 L113 73 Q108 53 101 53 Q83 63 61 58Z" fill="#ccd3e4" />
            <path d="M57 62 Q75 48 98 46 M66 41 Q85 35 101 42" fill="none" stroke="#f5eafa" strokeOpacity=".75" strokeWidth="3"/>
            <path d="M64 84 Q72 80 80 84 M91 84 Q99 80 107 84" fill="none" stroke="#50475d" strokeWidth="2.6" strokeLinecap="round" />
            <circle cx="73" cy="85" r="2" fill="#38384c"/><circle cx="99" cy="85" r="2" fill="#38384c"/>
            <path d="M85 88 L82 101 L89 103" stroke="#867785" fill="none" strokeWidth="1.6"/>
            <path d="M75 110 Q84 115 95 109" stroke="#79697b" fill="none" strokeWidth="1.7" strokeLinecap="round"/>
            <rect x="60" y="75" width="24" height="18" rx="7" fill="#a18dec" fillOpacity=".07" stroke="#d2c0ff" strokeWidth="1.4"/>
            <rect x="88" y="75" width="24" height="18" rx="7" fill="#a18dec" fillOpacity=".07" stroke="#d2c0ff" strokeWidth="1.4"/>
            <path d="M84 81 H89 M61 80 L54 79 M112 80 L119 79" stroke="#d2c0ff" strokeWidth="1.4" />
            <path d="M64 129 L85 150 L107 129" fill="none" stroke="#cbb3ff" strokeOpacity=".75" strokeWidth="2" />
            <path d="M85 149 V168" stroke="#b19def" strokeOpacity=".65" strokeWidth="2"/>
            <path d="M49 146 L62 153 M122 146 L109 153" stroke="#8a79c8" strokeWidth="2" />
          </>
        )}
        <path d="M39 145 Q84 156 130 145" stroke={accent} strokeOpacity=".18" fill="none"/>
      </g>
      <circle cx="84" cy="84" r="77" fill="none" stroke={accent} strokeOpacity=".55" strokeWidth="1.2"/>
      <path d="M30 23 Q84 -9 138 23" stroke={accent} strokeOpacity=".48" fill="none" strokeWidth="2" strokeLinecap="round"/>
      <circle cx="137" cy="23" r="2.5" fill={accent} />
    </svg>
  );
}
